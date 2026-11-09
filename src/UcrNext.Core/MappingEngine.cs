using System.Text.Json;

namespace UcrNext.Core;

/// <summary>
/// Evaluates all enabled bindings of all enabled profiles once per <see cref="Tick"/>.
/// The engine is state based: each tick it computes the complete desired output state and
/// only sends the differences, so keys can never get stuck when a binding is removed.
/// </summary>
public sealed class MappingEngine
{
    /// <summary>Mouse movement in pixels per tick at full deflection and sensitivity 1.</summary>
    public const double MousePixelsPerTick = 6.0;

    private readonly IInputSource _input;
    private readonly IKeyboardMouseOutput _km;
    private readonly IVirtualPadOutput? _pad;
    private readonly object _gate = new();

    private Binding[] _bindings = Array.Empty<Binding>();
    private readonly HashSet<int> _keysDown = new();
    private readonly HashSet<MouseButton> _mouseDown = new();
    private readonly PadState _lastPad = new();
    private bool _padUsed;
    private bool _padSent;
    private double _mouseRemX, _mouseRemY;

    public MappingEngine(IInputSource input, IKeyboardMouseOutput km, IVirtualPadOutput? pad)
    {
        _input = input; _km = km; _pad = pad;
    }

    /// <summary>Number of bindings currently active.</summary>
    public int ActiveBindingCount => _bindings.Length;

    /// <summary>Replaces the active bindings with a deep copy of the enabled ones.</summary>
    public void SetProfiles(IEnumerable<Profile> profiles)
    {
        var copy = profiles
            .Where(p => p.Enabled)
            .SelectMany(p => p.Bindings)
            .Where(b => b.Enabled && b.Input is not null && b.Output is not null)
            .Select(Clone)
            .ToArray();
        lock (_gate)
        {
            _bindings = copy;
            _padUsed = copy.Any(b => b.Output!.Target == OutputTarget.Xbox360);
        }
    }

    private bool _active = true;

    /// <summary>
    /// Turns output on/off. Turning it off releases everything; ticks are ignored until reactivated.
    /// </summary>
    public void SetActive(bool active)
    {
        lock (_gate)
        {
            _active = active;
            if (!active) ReleaseAll();
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            if (!_active) return;
            var keys = new HashSet<int>();
            var mouse = new HashSet<MouseButton>();
            var pad = new PadState();
            double mx = 0, my = 0;

            foreach (var b in _bindings)
            {
                if (!_input.TryRead(b.Input!, out double raw)) continue;
                double v = AxisProcessor.Shape(raw, b.Settings);
                var o = b.Output!;

                if (o.Kind == OutputKind.Button)
                {
                    if (v < b.Settings.ButtonThreshold) continue;
                    switch (o.Target)
                    {
                        case OutputTarget.Keyboard: keys.Add(o.Index); break;
                        case OutputTarget.Mouse: mouse.Add((MouseButton)o.Index); break;
                        case OutputTarget.Xbox360: pad.Buttons[o.Index] = true; break;
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
                        case OutputTarget.Mouse when o.Index == (int)MouseAxis.X: mx += v; break;
                        case OutputTarget.Mouse: my += v; break;
                    }
                }
            }

            ApplyKeys(keys);
            ApplyMouse(mouse, mx, my);
            ApplyPad(pad);
        }
    }

    /// <summary>Releases every held key/button and centers / unplugs the virtual pad.</summary>
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
            _mouseRemX = _mouseRemY = 0;
        }
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

    private static Binding Clone(Binding b) =>
        JsonSerializer.Deserialize<Binding>(JsonSerializer.Serialize(b))!;
}
