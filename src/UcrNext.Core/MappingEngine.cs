using System.Text.Json;

namespace UcrNext.Core;

/// <summary>
/// Evaluates all enabled bindings of all active profiles once per <see cref="Tick"/>.
/// A profile is active when it is enabled and either lists no applications or lists the
/// current <see cref="ForegroundApp"/>.
/// The engine is state based: each tick it computes the complete desired output state and
/// only sends the differences, so keys can never get stuck when a binding is removed.
/// </summary>
public sealed class MappingEngine
{
    /// <summary>Mouse movement in pixels per tick at full deflection and sensitivity 1.</summary>
    public const double MousePixelsPerTick = 6.0;

    private sealed record CompiledProfile(string Name, string[] Apps, DeviceMatch[] Skip, Binding[] Bindings);

    private static readonly HashSet<InputRef> NoBlocks = new();

    private readonly IInputSource _input;
    private readonly IKeyboardMouseOutput _km;
    private readonly IVirtualPadOutput? _pad;
    private readonly IVJoyOutput? _vjoy;
    private readonly object _gate = new();

    private CompiledProfile[] _profiles = Array.Empty<CompiledProfile>();
    private bool _profilesChanged = true;
    private string _activeKey = "\0";
    private string? _lastFg;
    private volatile IReadOnlyCollection<DeviceInfo> _connected = Array.Empty<DeviceInfo>();
    private IReadOnlyCollection<DeviceInfo>? _lastDevices;
    private string[] _skippedNames = Array.Empty<string>();
    private string _connectedKey = "";

    /// <summary>Profiles that apply to the foreground app but are skipped because a listed device is connected.</summary>
    public IReadOnlyList<string> SkippedProfileNames { get { lock (_gate) return _skippedNames; } }

    /// <summary>Tells the engine which devices are plugged in (for "skip if device present" rules).</summary>
    public void SetConnectedDevices(IEnumerable<DeviceInfo> devices)
    {
        var list = devices.ToList();
        string key = string.Join("\n", list.Select(d => d.DeviceId + "\t" + d.Name));
        if (key == _connectedKey) return;
        _connectedKey = key;
        _connected = list;
    }
    private Binding[] _bindings = Array.Empty<Binding>();
    private string[] _activeNames = Array.Empty<string>();
    private volatile HashSet<InputRef> _blocked = NoBlocks;
    private HashSet<InputRef> _blockCandidates = NoBlocks;

    private readonly HashSet<int> _keysDown = new();
    private readonly HashSet<MouseButton> _mouseDown = new();
    private readonly PadState _lastPad = new();
    private bool _padUsed;
    private bool _padSent;
    private readonly Dictionary<int, VJoyState> _lastVJoy = new();
    private double _mouseRemX, _mouseRemY;
    private bool _active = true;
    private volatile string? _foreground;

    public MappingEngine(IInputSource input, IKeyboardMouseOutput km, IVirtualPadOutput? pad, IVJoyOutput? vjoy = null)
    {
        _input = input; _km = km; _pad = pad; _vjoy = vjoy;
    }

    /// <summary>Executable name of the foreground program, e.g. "game.exe" (set by the host).</summary>
    public string? ForegroundApp
    {
        get => _foreground;
        set => _foreground = value;
    }

    /// <summary>Number of bindings currently active (after the per-application filter).</summary>
    public int ActiveBindingCount { get { lock (_gate) return _bindings.Length; } }

    /// <summary>Names of the profiles currently active (after the per-application filter).</summary>
    public IReadOnlyList<string> ActiveProfileNames { get { lock (_gate) return _activeNames; } }

    /// <summary>
    /// True if a physical input must not reach other programs right now. Thread-safe and
    /// lock-free: called from input hook callbacks.
    /// </summary>
    public bool IsBlocked(InputRef input) => _blocked.Contains(input);

    /// <summary>True if any active binding wants to block at all (cheap pre-check for hooks).</summary>
    public bool HasBlocks => _blocked.Count > 0;

    /// <summary>Replaces the profiles with a deep copy of the enabled ones.</summary>
    public void SetProfiles(IEnumerable<Profile> profiles)
    {
        var copy = profiles
            .Where(p => p.Enabled)
            .Select(p => new CompiledProfile(
                p.Name,
                p.Applications.Where(a => !string.IsNullOrWhiteSpace(a)).Select(Profile.NormalizeExe).ToArray(),
                p.SkipIfDevicesPresent.Select(m => new DeviceMatch { DeviceId = m.DeviceId, Name = m.Name }).ToArray(),
                p.Bindings.Where(b => b.Enabled && b.Input is not null && b.Output is not null).Select(Clone).ToArray()))
            .ToArray();
        lock (_gate)
        {
            _profiles = copy;
            _profilesChanged = true;
            RefreshActive();
        }
    }

    /// <summary>
    /// Turns output on/off. Turning it off releases everything and stops blocking; ticks are
    /// ignored until reactivated.
    /// </summary>
    public void SetActive(bool active)
    {
        lock (_gate)
        {
            _active = active;
            if (!active) ReleaseAll();
            else RefreshActive();
            _blocked = _active ? _blockCandidates : NoBlocks;
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (!_active) return;
            RefreshActive();

            var keys = new HashSet<int>();
            var mouse = new HashSet<MouseButton>();
            var pad = new PadState();
            var vjoy = new Dictionary<int, VJoyState>();
            double mx = 0, my = 0;

            foreach (var b in _bindings)
            {
                if (!_input.TryRead(b.Input!, out double raw)) continue;
                var o = b.Output!;
                var s = b.Settings;
                double v;
                bool pressed;
                if (b.Input!.Kind == InputKind.Button)
                {
                    // Buttons: Invert means "active while released"; driving an axis uses the
                    // configured pressed/released values.
                    pressed = (raw >= 0.5) != s.Invert;
                    v = pressed ? s.ButtonAxisPressed : s.ButtonAxisReleased;
                }
                else
                {
                    v = AxisProcessor.Shape(raw, s);
                    pressed = v >= s.ButtonThreshold;
                    if (o.IsTrigger && s.TriggerFullRange && b.Input.Kind == InputKind.Axis) v = (v + 1) / 2;
                }

                if (o.Kind == OutputKind.Button)
                {
                    if (!pressed) continue;
                    switch (o.Target)
                    {
                        case OutputTarget.Keyboard: keys.Add(o.Index); break;
                        case OutputTarget.Mouse: mouse.Add((MouseButton)o.Index); break;
                        case OutputTarget.Xbox360: pad.Buttons[o.Index] = true; break;
                        case OutputTarget.VJoy when o.Index is >= 0 and < VJoyState.MaxButtons:
                            VJoyFor(vjoy, o.Device).Buttons[o.Index] = true; break;
                    }
                }
                else
                {
                    if (o.IsTrigger) v = Math.Max(0, v);
                    switch (o.Target)
                    {
                        case OutputTarget.Xbox360:
                            // Several bindings on one axis: the strongest deflection wins.
                            if (Math.Abs(v) > Math.Abs(pad.Axes[o.Index])) pad.Axes[o.Index] = v;
                            break;
                        case OutputTarget.VJoy:
                            var vs = VJoyFor(vjoy, o.Device);
                            if (Math.Abs(v) > Math.Abs(vs.Axes[o.Index])) vs.Axes[o.Index] = v;
                            break;
                        case OutputTarget.Mouse when o.Index == (int)MouseAxis.X: mx += v; break;
                        case OutputTarget.Mouse: my += v; break;
                    }
                }
            }

            ApplyKeys(keys);
            ApplyMouse(mouse, mx, my);
            ApplyPad(pad);
            ApplyVJoy(vjoy);
        }
    }

    /// <summary>Releases every held key/button and centres / unplugs the virtual devices.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            ApplyKeys(new HashSet<int>());
            ApplyMouse(new HashSet<MouseButton>(), 0, 0);
            if (_padSent && _pad is not null)
            {
                _pad.Update(new PadState());
                _pad.Disconnect();
            }
            _lastPad.CopyFrom(new PadState());
            _padSent = false;
            foreach (var id in _lastVJoy.Keys) _vjoy?.Release(id);
            _lastVJoy.Clear();
            _mouseRemX = _mouseRemY = 0;
            _blocked = NoBlocks;
        }
    }

    /// <summary>Recomputes which profiles apply to the current foreground app (cheap when nothing changed).</summary>
    private void RefreshActive()
    {
        string? fg = _foreground;
        var devices = _connected;
        if (!_profilesChanged && fg == _lastFg && ReferenceEquals(devices, _lastDevices)) return;
        _lastFg = fg;
        _lastDevices = devices;
        var forApp = Enumerable.Range(0, _profiles.Length).Where(i => Profile.AppliesTo(_profiles[i].Apps, fg)).ToArray();
        var skipped = forApp.Where(i => Profile.SkippedByDevices(_profiles[i].Skip, devices)).ToArray();
        var activeIdx = forApp.Except(skipped).ToArray();
        var active = activeIdx.Select(i => _profiles[i]).ToArray();
        string key = string.Join(",", activeIdx) + "|" + string.Join(",", skipped);
        if (!_profilesChanged && key == _activeKey) return;
        _profilesChanged = false;
        _activeKey = key;

        _bindings = active.SelectMany(p => p.Bindings).ToArray();
        _activeNames = active.Select(p => p.Name).ToArray();
        _skippedNames = skipped.Select(i => _profiles[i].Name).ToArray();
        _padUsed = _bindings.Any(b => b.Output!.Target == OutputTarget.Xbox360);
        var blocks = _bindings.Where(b => b.Block).Select(b => b.Input!).ToHashSet();
        _blockCandidates = blocks.Count == 0 ? NoBlocks : blocks;
        _blocked = _active ? _blockCandidates : NoBlocks;
    }

    private static VJoyState VJoyFor(Dictionary<int, VJoyState> map, int device)
    {
        if (!map.TryGetValue(device, out var s)) map[device] = s = new VJoyState();
        return s;
    }

    private void ApplyKeys(HashSet<int> want)
    {
        foreach (var k in _keysDown.Except(want).ToList()) { _km.Key(k, false); _keysDown.Remove(k); }
        foreach (var k in want.Except(_keysDown).ToList()) { _km.Key(k, true); _keysDown.Add(k); }
    }

    private void ApplyMouse(HashSet<MouseButton> want, double vx, double vy)
    {
        foreach (var b in _mouseDown.Except(want).ToList()) { _km.MouseButton(b, false); _mouseDown.Remove(b); }
        foreach (var b in want.Except(_mouseDown).ToList()) { _km.MouseButton(b, true); _mouseDown.Add(b); }

        // Keep fractional pixels so slow movement still moves.
        _mouseRemX += Math.Clamp(vx, -1, 1) * MousePixelsPerTick;
        _mouseRemY += Math.Clamp(vy, -1, 1) * MousePixelsPerTick;
        int dx = (int)Math.Truncate(_mouseRemX), dy = (int)Math.Truncate(_mouseRemY);
        if (vx == 0) _mouseRemX = 0; else _mouseRemX -= dx;
        if (vy == 0) _mouseRemY = 0; else _mouseRemY -= dy;
        if (dx != 0 || dy != 0) _km.MouseMove(dx, dy);
    }

    private void ApplyPad(PadState pad)
    {
        if (_pad is null) return;
        if (!_padUsed)
        {
            if (_padSent) { _pad.Update(new PadState()); _pad.Disconnect(); _padSent = false; _lastPad.CopyFrom(new PadState()); }
            return;
        }
        if (_padSent && pad.Equals(_lastPad)) return;
        _pad.Update(pad);
        _lastPad.CopyFrom(pad);
        _padSent = true;
    }

    private void ApplyVJoy(Dictionary<int, VJoyState> want)
    {
        if (_vjoy is null) return;
        // Devices used by the active bindings stay acquired even while all their inputs are idle.
        foreach (var b in _bindings)
            if (b.Output!.Target == OutputTarget.VJoy) VJoyFor(want, b.Output.Device);

        foreach (var id in _lastVJoy.Keys.Except(want.Keys).ToList())
        {
            _vjoy.Release(id);
            _lastVJoy.Remove(id);
        }
        foreach (var (id, state) in want)
        {
            if (_lastVJoy.TryGetValue(id, out var last) && last.Equals(state)) continue;
            _vjoy.Update(id, state);
            _lastVJoy[id] = state.Clone();
        }
    }

    private static Binding Clone(Binding b) =>
        JsonSerializer.Deserialize<Binding>(JsonSerializer.Serialize(b))!;
}
