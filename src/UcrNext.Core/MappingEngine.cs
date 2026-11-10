using System.Text.Json;

namespace UcrNext.Core;

/// <summary>
/// Evaluates all enabled bindings of all active profiles once per <see cref="Tick"/>.
/// A profile is active when it is enabled, applies to the current <see cref="ForegroundApp"/>,
/// and none of its "skip while connected" devices is plugged in.
/// The engine is state based: each tick it computes the complete desired output state and
/// only sends the differences, so keys can never get stuck when a binding is removed.
/// Bindings in timed modes (long press, delta timeouts, debounce, increment, trim) keep a small
/// per-binding state that is reset whenever the profiles change.
/// </summary>
public sealed class MappingEngine
{
    /// <summary>Mouse movement in pixels per tick at full deflection and sensitivity 1.</summary>
    public const double MousePixelsPerTick = 6.0;
    /// <summary>Wheel notches per tick at full deflection (20 per second at the usual 4 ms tick).</summary>
    public const double WheelNotchesPerTick = 0.08;
    public const int WheelNotch = 120;

    private sealed record CompiledProfile(string Name, string[] Apps, DeviceMatch[] Skip, Binding[] Bindings)
    {
        /// <summary>Named states (UCR filters) of this profile; set by SetState bindings.</summary>
        public Dictionary<string, bool> States { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Runtime state for bindings whose output depends on time or history.</summary>
    private sealed class BindingState
    {
        public double Out;
        public double LastIn;
        public long LastMoveMs = long.MinValue;
        public bool Moved;
        public int Target, Applied;
        public long ChangeAtMs = long.MaxValue;
        public bool WasPressed, LongHeld;
        public long PressStartMs, ShortUntilMs = long.MinValue;
        public double TrimX, TrimY;
        public bool TrimTaken;
        public long LastTickMs = long.MinValue;
        public bool Toggled, Initialized, Touched;
        public double FirstV = double.NaN;
    }

    private static readonly HashSet<InputRef> NoBlocks = new();

    private readonly IInputSource _input;
    private readonly IDeltaSource? _delta;
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

    private Binding[] _bindings = Array.Empty<Binding>();
    private CompiledProfile[] _owners = Array.Empty<CompiledProfile>(); // profile of each binding
    private HashSet<OutputRef> _defaultOutputs = new();
    private readonly Dictionary<Binding, BindingState> _states = new(ReferenceEqualityComparer.Instance);
    private string[] _activeNames = Array.Empty<string>();
    private volatile HashSet<InputRef> _blocked = NoBlocks;
    private HashSet<InputRef> _blockCandidates = NoBlocks;

    private readonly HashSet<int> _keysDown = new();
    private readonly HashSet<MouseButton> _mouseDown = new();
    private readonly HashSet<MouseButton> _wheelHeld = new();
    private double _wheelRem, _hwheelRem;
    private readonly Dictionary<int, PadState> _lastPads = new();
    private int _padsNeeded; // pads 0.._padsNeeded-1 are plugged in (keeps P1..P4 numbering stable)
    private readonly Dictionary<int, VJoyState> _lastVJoy = new();
    private double _mouseRemX, _mouseRemY;
    private bool _active = true;
    private volatile string? _foreground;

    public MappingEngine(IInputSource input, IKeyboardMouseOutput km, IVirtualPadOutput? pad, IVJoyOutput? vjoy = null)
    {
        _input = input; _km = km; _pad = pad; _vjoy = vjoy;
        _delta = input as IDeltaSource;
    }

    /// <summary>Milliseconds clock (replaceable in tests).</summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;

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

    /// <summary>Profiles that apply to the foreground app but are skipped because a listed device is connected.</summary>
    public IReadOnlyList<string> SkippedProfileNames { get { lock (_gate) return _skippedNames; } }

    /// <summary>
    /// True if a physical input must not reach other programs right now. Thread-safe and
    /// lock-free: called from input hook callbacks.
    /// </summary>
    public bool IsBlocked(InputRef input) => _blocked.Contains(input);

    /// <summary>True if any active binding wants to block at all (cheap pre-check for hooks).</summary>
    public bool HasBlocks => _blocked.Count > 0;

    /// <summary>Tells the engine which devices are plugged in (for "skip if device present" rules).</summary>
    public void SetConnectedDevices(IEnumerable<DeviceInfo> devices)
    {
        var list = devices.ToList();
        string key = string.Join("\n", list.Select(d => d.DeviceId + "\t" + d.Name));
        if (key == _connectedKey) return;
        _connectedKey = key;
        _connected = list;
    }

    /// <summary>Replaces the profiles with a deep copy of the enabled ones.</summary>
    public void SetProfiles(IEnumerable<Profile> profiles)
    {
        var copy = profiles
            .Where(p => p.Enabled)
            .Select(p => new CompiledProfile(
                p.Name,
                p.Applications.Where(a => !string.IsNullOrWhiteSpace(a)).Select(Profile.NormalizeExe).ToArray(),
                p.SkipIfDevicesPresent.Select(m => new DeviceMatch { DeviceId = m.DeviceId, Name = m.Name }).ToArray(),
                p.Bindings.Where(b => b.Enabled && b.IsComplete).Select(Clone).ToArray()))
            .ToArray();
        lock (_gate)
        {
            _profiles = copy;
            _profilesChanged = true;
            _states.Clear();
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
            long now = Clock();

            var keys = new HashSet<int>();
            var mouse = new HashSet<MouseButton>();
            var pads = new Dictionary<int, PadState>();
            var vjoy = new Dictionary<int, VJoyState>();
            var wheelButtons = new HashSet<MouseButton>();
            double mx = 0, my = 0, px = 0, py = 0, wheel = 0, hwheel = 0;
            HashSet<OutputRef>? touched = _defaultOutputs.Count > 0 ? new() : null;

            // States first, so the other bindings see this tick's states.
            for (int i = 0; i < _bindings.Length; i++)
                if (_bindings[i].Mode == BindingMode.SetState && Allowed(i)) RunStateSwitch(_bindings[i], _owners[i].States);

            for (int i = 0; i < _bindings.Length; i++)
            {
                var b = _bindings[i];
                if (b.Mode is BindingMode.SetState or BindingMode.DefaultValue || !Allowed(i)) continue;
                if (!Evaluate(b, now, out bool pressed, out double v)) continue;
                var o = b.Output!;

                if (o.Kind == OutputKind.Button)
                {
                    if (!pressed) continue;
                    switch (o.Target)
                    {
                        case OutputTarget.Keyboard: keys.Add(o.Index); break;
                        case OutputTarget.Mouse when o.Index >= (int)MouseButton.WheelUp: wheelButtons.Add((MouseButton)o.Index); break;
                        case OutputTarget.Mouse: mouse.Add((MouseButton)o.Index); break;
                        case OutputTarget.Xbox360: PadFor(pads, o.Device).Buttons[o.Index] = true; break;
                        case OutputTarget.VJoy when o.Index is >= 0 and < VJoyState.MaxButtons:
                            VJoyFor(vjoy, o.Device).Buttons[o.Index] = true; break;
                    }
                }
                else
                {
                    if (o.IsTrigger) v = Math.Max(0, v);
                    if (touched is not null && _defaultOutputs.Contains(o))
                    {
                        // A starting value holds until a binding on the same output moves.
                        var st = State(b);
                        if (double.IsNaN(st.FirstV)) st.FirstV = v;
                        else if (Math.Abs(v - st.FirstV) > 0.02) st.Touched = true;
                        if (st.Touched) touched.Add(o);
                    }
                    switch (o.Target)
                    {
                        case OutputTarget.Xbox360:
                            // Several bindings on one axis: the strongest deflection wins.
                            var ps = PadFor(pads, o.Device);
                            if (Math.Abs(v) > Math.Abs(ps.Axes[o.Index])) ps.Axes[o.Index] = v;
                            break;
                        case OutputTarget.VJoy:
                            var vs = VJoyFor(vjoy, o.Device);
                            if (Math.Abs(v) > Math.Abs(vs.Axes[o.Index])) vs.Axes[o.Index] = v;
                            break;
                        case OutputTarget.Mouse when b.Mode == BindingMode.AxisToDelta && o.Index == (int)MouseAxis.X: px += State(b).Out; break;
                        case OutputTarget.Mouse when b.Mode == BindingMode.AxisToDelta && o.Index == (int)MouseAxis.Y: py += State(b).Out; break;
                        case OutputTarget.Mouse when o.Index == (int)MouseAxis.X: mx += v; break;
                        case OutputTarget.Mouse when o.Index == (int)MouseAxis.Y: my += v; break;
                        case OutputTarget.Mouse when o.Index == (int)MouseAxis.Wheel: wheel += v; break;
                        case OutputTarget.Mouse when o.Index == (int)MouseAxis.HWheel: hwheel += v; break;
                    }
                }
            }

            // Starting values (DefaultValue bindings) for outputs nothing has moved yet.
            for (int i = 0; i < _bindings.Length; i++)
            {
                var b = _bindings[i];
                if (b.Mode != BindingMode.DefaultValue || !Allowed(i) || b.Output!.Kind != OutputKind.Axis) continue;
                if (touched is not null && touched.Contains(b.Output)) continue;
                double dv = Math.Clamp(b.Settings.DefaultValue, b.Output.IsTrigger ? 0 : -1, 1);
                if (b.Output.Target == OutputTarget.Xbox360) PadFor(pads, b.Output.Device).Axes[b.Output.Index] = dv;
                else if (b.Output.Target == OutputTarget.VJoy) VJoyFor(vjoy, b.Output.Device).Axes[b.Output.Index] = dv;
            }

            ApplyKeys(keys);
            ApplyMouse(mouse, mx, my, px, py);
            ApplyWheel(wheelButtons, wheel, hwheel);
            ApplyPads(pads);
            ApplyVJoy(vjoy);
        }
    }

    // ------------------------------------------------------------------ evaluation

    private double Read(InputRef? r) => r is not null && _input.TryRead(r, out var v) ? v : 0;
    private bool Down(InputRef? r) => Read(r) >= 0.5;

    private (double Sum, int Events) ReadDelta(InputRef r)
    {
        if (_delta is not null && _delta.TryReadDelta(r, out var sum, out var n)) return (sum, n);
        double v = Read(r);
        return (v, v != 0 ? 1 : 0);
    }

    private BindingState State(Binding b)
    {
        if (!_states.TryGetValue(b, out var s)) _states[b] = s = new BindingState();
        return s;
    }

    /// <summary>Computes this tick's (pressed, value) for a binding. False = input not available.</summary>
    private bool Evaluate(Binding b, long now, out bool pressed, out double v)
    {
        var s = b.Settings;
        var input = b.Input!;
        pressed = false;
        v = 0;

        switch (b.Mode)
        {
            case BindingMode.ButtonsToAxis:
            {
                bool hi = Down(input), lo = Down(b.Extra(0));
                v = hi && !lo ? 1 : lo && !hi ? -1 : 0;
                if (s.Invert) v = -v;
                pressed = v >= s.ButtonThreshold;
                return true;
            }

            case BindingMode.StickPair:
            {
                var (x, y) = ProcessPair(Read(input), Read(b.Extra(0)), s);
                (x, y) = Rotate(x, y, s.RotationDegrees);
                v = s.Component == PairComponent.X ? x : y;
                pressed = v >= s.ButtonThreshold;
                return true;
            }

            case BindingMode.StickTrim:
            {
                var st = State(b);
                double rx = Read(input), ry = Read(b.Extra(0));
                if (Down(b.Extra(2)))
                {
                    st.TrimX = st.TrimY = 0;          // reset trim; output holds
                }
                else if (Down(b.Extra(1)))
                {
                    if (!st.TrimTaken)
                    {
                        st.TrimX += rx; st.TrimY += ry; // add the current position as trim; output holds
                        st.TrimTaken = true;
                    }
                }
                else
                {
                    st.TrimTaken = false;
                    var plain = new AxisSettings
                    {
                        Deadzone = s.Deadzone, CircularDeadzone = s.CircularDeadzone,
                        Sensitivity = s.Sensitivity, NonLinearSensitivity = s.NonLinearSensitivity,
                    };
                    var (x, y) = ProcessPair(rx, ry, plain);
                    x = Math.Clamp(x + st.TrimX, -1, 1);
                    y = Math.Clamp(y + st.TrimY, -1, 1);
                    if (s.Invert) x = -x;
                    if (s.InvertY) y = -y;
                    st.Out = s.Component == PairComponent.X ? x : y;
                }
                v = st.Out;
                pressed = v >= s.ButtonThreshold;
                return true;
            }

            case BindingMode.AxesRange:
            {
                double x = Read(input), y = Read(b.Extra(0));
                if (s.Invert) x = -x;
                if (s.InvertY) y = -y;
                pressed = x >= s.XStart && x <= s.XEnd && y >= s.YStart && y <= s.YEnd;
                v = pressed ? 1 : 0;
                return true;
            }

            case BindingMode.DeltaToAxis:
            {
                var st = State(b);
                var (sum, n) = ReadDelta(input);
                double avg = n > 0 ? sum / n : 0;
                if (s.DeltaMode == DeltaMode.Relative)
                {
                    if (Down(b.Extra(0))) st.Out = 0;
                    else if (n > 0 && Math.Abs(avg) >= s.DeltaDeadzone)
                        st.Out = Math.Clamp(st.Out + sum * s.DeltaSensitivity / 32768.0, -1, 1);
                }
                else
                {
                    if (n > 0 && Math.Abs(avg) >= s.DeltaDeadzone)
                    {
                        st.Out = Math.Clamp(avg * 100 * s.DeltaSensitivity / 32768.0, -1, 1);
                        st.LastMoveMs = now;
                    }
                    else if (now - st.LastMoveMs > s.TimeoutMs) st.Out = 0;
                }
                v = s.Invert ? -st.Out : st.Out;
                pressed = v >= s.ButtonThreshold;
                return true;
            }

            case BindingMode.DeltaToButtons:
            {
                var st = State(b);
                var (sum, n) = ReadDelta(input);
                double avg = n > 0 ? sum / n : 0;
                if (n > 0 && avg != 0 && Math.Abs(avg) >= s.DeltaDeadzone)
                {
                    st.LastMoveMs = now; st.Moved = true;
                    SetTarget(st, Math.Sign(avg), now, s.DebounceMs);
                }
                else if (st.Moved && now - st.LastMoveMs > s.TimeoutMs)
                {
                    st.Moved = false;
                    SetTarget(st, 0, now, s.DebounceMs);
                }
                if (now >= st.ChangeAtMs) { st.Applied = st.Target; st.ChangeAtMs = long.MaxValue; }
                pressed = st.Applied != 0 && st.Applied == Math.Sign(s.Direction);
                v = pressed ? 1 : 0;
                return true;
            }

            case BindingMode.LongPress:
            {
                var st = State(b);
                bool down = Down(input);
                if (down && !st.WasPressed) { st.PressStartMs = now; st.LongHeld = false; }
                if (down && !st.LongHeld && now - st.PressStartMs >= s.LongPressMs) st.LongHeld = true;
                if (!down && st.WasPressed)
                {
                    if (st.LongHeld) st.LongHeld = false;
                    else st.ShortUntilMs = now + s.ShortPressMs;
                }
                st.WasPressed = down;
                pressed = s.Component == PairComponent.X ? now < st.ShortUntilMs : st.LongHeld && down;
                v = pressed ? 1 : 0;
                return true;
            }

            case BindingMode.Increment:
            {
                var st = State(b);
                double inV = Read(input);
                if (s.Invert) inV = -inV;
                inV = AxisProcessor.Deadzone(inV, s.Deadzone);
                inV = Math.Clamp(AxisProcessor.ApplySensitivity(inV, s), -1, 1);
                double dtTicks = st.LastTickMs == long.MinValue ? 0 : (now - st.LastTickMs) / 10.0; // UCR steps every 10 ms
                st.LastTickMs = now;
                if (Math.Sign(inV) != Math.Sign(st.Out) && Math.Abs(inV) < Math.Abs(st.LastIn))
                    inV *= Math.Truncate(s.CounterEffect); // UCR casts the counter effect to an integer
                st.Out = Math.Clamp(st.Out + inV * (s.IncrementRate / 100.0) * dtTicks, -1, 1);
                st.LastIn = inV;
                v = st.Out;
                pressed = v >= s.ButtonThreshold;
                return true;
            }

            case BindingMode.AxisMerger:
            {
                double hi = Read(input), lo = Read(b.Extra(0));
                if (s.Invert) hi = -hi;
                if (s.InvertY) lo = -lo;
                v = s.MergeMode switch
                {
                    MergeMode.Greatest => Math.Abs(hi) > Math.Abs(lo) ? hi : lo,
                    MergeMode.Sum => Math.Clamp(hi + lo, -1, 1),
                    _ => (hi + lo) / 2,
                };
                v = Math.Clamp(AxisProcessor.ApplySensitivity(AxisProcessor.Deadzone(v, s.Deadzone), s), -1, 1);
                pressed = v >= s.ButtonThreshold;
                if (b.Output!.IsTrigger && s.TriggerFullRange) v = (v + 1) / 2;
                return true;
            }

            case BindingMode.AxisToDelta:
            {
                var st = State(b);
                double a = AxisProcessor.Deadzone(Read(input), s.Deadzone);
                if (s.Invert) a = -a;
                a = Math.Clamp(AxisProcessor.ApplySensitivity(a, s), -1, 1);
                double dt = st.LastTickMs == long.MinValue ? 0 : now - st.LastTickMs;
                st.LastTickMs = now;
                // UCR sends Min..Max pixels every 10 ms; spread that over our ticks.
                double per10 = a == 0 ? 0 : s.MinDelta + Math.Abs(a) * Math.Max(0, s.MaxDelta - s.MinDelta);
                st.Out = Math.Sign(a) * per10 * dt / 10.0;
                v = a;
                pressed = v >= s.ButtonThreshold;
                return true;
            }

            case BindingMode.FpvMix:
            {
                double roll = Read(input), yaw = Read(b.Extra(0));
                if (s.Invert) roll = -roll;
                if (s.InvertY) yaw = -yaw;
                double mix = Math.Clamp(s.CameraAngle, 0, 90) / 90;
                v = s.Component == PairComponent.X
                    ? roll * (1 - mix) + yaw * mix
                    : yaw * (1 - mix) - roll * mix;
                v = Math.Clamp(v, -1, 1);
                pressed = v >= s.ButtonThreshold;
                return true;
            }

            case BindingMode.ReversibleThrottle:
            {
                var st = State(b);
                bool rev = Down(b.Extra(0));
                if (rev && !st.WasPressed) st.Toggled = !st.Toggled;
                st.WasPressed = rev;
                double t = Read(input);
                if (s.Invert) t = input.Kind == InputKind.Trigger ? 1 - t : -t;
                double half = input.Kind == InputKind.Trigger ? Math.Clamp(t, 0, 1) : (Math.Clamp(t, -1, 1) + 1) / 2;
                v = st.Toggled ? -half : half;
                if (s.InvertOutput) v = -v;
                pressed = v >= s.ButtonThreshold;
                return true;
            }

            default: // Direct
            {
                if (!_input.TryRead(input, out double raw)) return false;
                if (input.Kind == InputKind.Button)
                {
                    // Buttons: Invert means "active while released"; driving an axis uses the
                    // configured pressed/released values.
                    bool down = raw >= 0.5;
                    if (s.Toggle)
                    {
                        var st = State(b);
                        if (down && !st.WasPressed) st.Toggled = !st.Toggled;
                        st.WasPressed = down;
                        pressed = st.Toggled != s.Invert;
                    }
                    else if (s.PulseMs > 0)
                    {
                        var st = State(b);
                        bool edge = s.PulseOnRelease ? !down && st.WasPressed : down && !st.WasPressed;
                        if (edge) st.ShortUntilMs = now + s.PulseMs;
                        st.WasPressed = down;
                        pressed = now < st.ShortUntilMs;
                    }
                    else pressed = down != s.Invert;
                    v = pressed ? s.ButtonAxisPressed : s.ButtonAxisReleased;
                }
                else if (s.Half != AxisHalf.Full)
                {
                    v = AxisProcessor.Split(AxisProcessor.Deadzone(raw, s.Deadzone), s.Half);
                    if (s.Invert) v = -v;
                    pressed = v >= s.ButtonThreshold;
                }
                else
                {
                    v = AxisProcessor.Shape(raw, s);
                    pressed = v >= s.ButtonThreshold;
                    if (b.Output!.IsTrigger && s.TriggerFullRange && input.Kind == InputKind.Axis) v = (v + 1) / 2;
                }
                return true;
            }
        }
    }

    /// <summary>True if the binding's state conditions hold (UCR: a plugin is filtered unless state ^ negative).</summary>
    private bool Allowed(int i)
    {
        var conditions = _bindings[i].Conditions;
        if (conditions.Count == 0) return true;
        var states = _owners[i].States;
        foreach (var c in conditions)
        {
            states.TryGetValue(c.Name.Trim(), out bool on);
            if (on == c.Not) return false;
        }
        return true;
    }

    /// <summary>SetState: a button press/release, or an axis entering/leaving XStart..XEnd, changes a named state.</summary>
    private void RunStateSwitch(Binding b, Dictionary<string, bool> states)
    {
        var s = b.Settings;
        string name = s.StateName.Trim();
        if (name.Length == 0 || b.Input is null) return;
        var st = State(b);
        bool button = b.Input.Kind == InputKind.Button;
        bool Inside(double x) => x >= s.XStart && x <= s.XEnd;
        if (!st.Initialized)
        {
            st.Initialized = true;
            st.WasPressed = !button && Inside(0); // UCR starts an axis at centre
        }
        bool active;
        if (button) active = Down(b.Input) != s.Invert;
        else
        {
            double x = Read(b.Input);
            active = Inside(s.Invert ? -x : x);
        }
        if (active == st.WasPressed) return;
        st.WasPressed = active;
        states.TryGetValue(name, out bool cur);
        switch (active ? s.StateOnPress : s.StateOnRelease)
        {
            case StateAction.On: states[name] = true; break;
            case StateAction.Off: states[name] = false; break;
            case StateAction.Toggle: states[name] = !cur; break;
        }
    }

    /// <summary>States that are on in the active profiles, as "profile: state" (for status displays).</summary>
    public IReadOnlyList<string> ActiveStates
    {
        get
        {
            lock (_gate)
                return _owners.Distinct().SelectMany(p => p.States.Where(kv => kv.Value).Select(kv => $"{p.Name}: {kv.Key}")).ToList();
        }
    }

    private static void SetTarget(BindingState st, int target, long now, int debounceMs)
    {
        if (target == st.Target) return;
        st.Target = target;
        st.ChangeAtMs = now + Math.Max(0, debounceMs);
    }

    /// <summary>Deadzone (per axis or circular) + sensitivity + invert for a stick pair.</summary>
    private static (double X, double Y) ProcessPair(double x, double y, AxisSettings s)
    {
        if (s.Deadzone > 0)
        {
            if (s.CircularDeadzone) (x, y) = AxisProcessor.CircularDeadzone(x, y, s.Deadzone);
            else { x = AxisProcessor.Deadzone(x, s.Deadzone); y = AxisProcessor.Deadzone(y, s.Deadzone); }
        }
        x = Math.Clamp(AxisProcessor.ApplySensitivity(x, s), -1, 1);
        y = Math.Clamp(AxisProcessor.ApplySensitivity(y, s), -1, 1);
        if (s.Invert) x = -x;
        if (s.InvertY) y = -y;
        return (x, y);
    }

    private static (double X, double Y) Rotate(double x, double y, double degrees)
    {
        if (degrees == 0) return (x, y);
        double r = degrees * Math.PI / 180, c = Math.Cos(r), sn = Math.Sin(r);
        return (Math.Clamp(c * x - sn * y, -1, 1), Math.Clamp(sn * x + c * y, -1, 1));
    }

    // ------------------------------------------------------------------ outputs

    /// <summary>Releases every held key/button and centres / unplugs the virtual devices.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            ApplyKeys(new HashSet<int>());
            ApplyMouse(new HashSet<MouseButton>(), 0, 0, 0, 0);
            ApplyWheel(new HashSet<MouseButton>(), 0, 0);
            if (_pad is not null)
                foreach (var pad in _lastPads.Keys.OrderByDescending(p => p))
                {
                    _pad.Update(pad, new PadState());
                    _pad.Disconnect(pad);
                }
            _lastPads.Clear();
            foreach (var id in _lastVJoy.Keys) _vjoy?.Release(id);
            _lastVJoy.Clear();
            _mouseRemX = _mouseRemY = 0;
            _states.Clear();
            _blocked = NoBlocks;
        }
    }

    /// <summary>Recomputes which profiles apply right now (cheap when nothing changed).</summary>
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
        _owners = active.SelectMany(p => p.Bindings.Select(_ => p)).ToArray();
        _defaultOutputs = _bindings.Where(b => b.Mode == BindingMode.DefaultValue).Select(b => b.Output!).ToHashSet();
        _activeNames = active.Select(p => p.Name).ToArray();
        _skippedNames = skipped.Select(i => _profiles[i].Name).ToArray();
        var padIndexes = _bindings.Where(b => b.Output?.Target == OutputTarget.Xbox360)
            .Select(b => Math.Clamp(b.Output!.Device, 0, OutputRef.MaxXboxPads - 1)).ToList();
        _padsNeeded = padIndexes.Count == 0 ? 0 : padIndexes.Max() + 1;
        var blocks = _bindings.Where(b => b.Block && b.Input is not null).Select(b => b.Input!).ToHashSet();
        _blockCandidates = blocks.Count == 0 ? NoBlocks : blocks;
        _blocked = _active ? _blockCandidates : NoBlocks;
    }

    private static VJoyState VJoyFor(Dictionary<int, VJoyState> map, int device)
    {
        if (!map.TryGetValue(device, out var s)) map[device] = s = new VJoyState();
        return s;
    }

    private static PadState PadFor(Dictionary<int, PadState> map, int pad)
    {
        pad = Math.Clamp(pad, 0, OutputRef.MaxXboxPads - 1);
        if (!map.TryGetValue(pad, out var s)) map[pad] = s = new PadState();
        return s;
    }

    private void ApplyKeys(HashSet<int> want)
    {
        foreach (var k in _keysDown.Except(want).ToList()) { _km.Key(k, false); _keysDown.Remove(k); }
        foreach (var k in want.Except(_keysDown).ToList()) { _km.Key(k, true); _keysDown.Add(k); }
    }

    /// <summary>Mouse buttons, plus movement: <paramref name="vx"/>/<paramref name="vy"/> as -1..1 deflection, <paramref name="px"/>/<paramref name="py"/> in pixels (Axis to Delta).</summary>
    private void ApplyMouse(HashSet<MouseButton> want, double vx, double vy, double px, double py)
    {
        foreach (var b in _mouseDown.Except(want).ToList()) { _km.MouseButton(b, false); _mouseDown.Remove(b); }
        foreach (var b in want.Except(_mouseDown).ToList()) { _km.MouseButton(b, true); _mouseDown.Add(b); }

        // Keep fractional pixels so slow movement still moves.
        _mouseRemX += Math.Clamp(vx, -1, 1) * MousePixelsPerTick + px;
        _mouseRemY += Math.Clamp(vy, -1, 1) * MousePixelsPerTick + py;
        int dx = (int)Math.Truncate(_mouseRemX), dy = (int)Math.Truncate(_mouseRemY);
        if (vx == 0 && px == 0) _mouseRemX = 0; else _mouseRemX -= dx;
        if (vy == 0 && py == 0) _mouseRemY = 0; else _mouseRemY -= dy;
        if (dx != 0 || dy != 0) _km.MouseMove(dx, dy);
    }

    /// <summary>Wheel "buttons" scroll one notch when pressed; wheel axes scroll continuously.</summary>
    private void ApplyWheel(HashSet<MouseButton> want, double wheel, double hwheel)
    {
        foreach (var b in want.Except(_wheelHeld))
            switch (b)
            {
                case MouseButton.WheelUp: _km.MouseWheel(WheelNotch, false); break;
                case MouseButton.WheelDown: _km.MouseWheel(-WheelNotch, false); break;
                case MouseButton.WheelRight: _km.MouseWheel(WheelNotch, true); break;
                case MouseButton.WheelLeft: _km.MouseWheel(-WheelNotch, true); break;
            }
        _wheelHeld.Clear();
        _wheelHeld.UnionWith(want);

        static int Notches(ref double rem, double v)
        {
            if (v == 0) { rem = 0; return 0; }
            rem += Math.Clamp(v, -1, 1) * WheelNotchesPerTick;
            int n = (int)Math.Truncate(rem + Math.Sign(rem) * 1e-9); // 0.08 × 50 is 3.99999…
            rem -= n;
            return n;
        }
        int nv = Notches(ref _wheelRem, wheel), nh = Notches(ref _hwheelRem, hwheel);
        if (nv != 0) _km.MouseWheel(nv * WheelNotch, false);
        if (nh != 0) _km.MouseWheel(nh * WheelNotch, true);
    }

    /// <summary>
    /// Plugs in pads 0..N-1 in order (so a game always sees P1 before P2), sends changed states,
    /// and unplugs pads no longer needed (highest first).
    /// </summary>
    private void ApplyPads(Dictionary<int, PadState> want)
    {
        if (_pad is null) return;
        foreach (var pad in _lastPads.Keys.Where(p => p >= _padsNeeded).OrderByDescending(p => p).ToList())
        {
            _pad.Update(pad, new PadState());
            _pad.Disconnect(pad);
            _lastPads.Remove(pad);
        }
        for (int pad = 0; pad < _padsNeeded; pad++)
        {
            var state = want.TryGetValue(pad, out var s) ? s : new PadState();
            if (_lastPads.TryGetValue(pad, out var last) && last.Equals(state)) continue;
            _pad.Update(pad, state);
            var copy = new PadState();
            copy.CopyFrom(state);
            _lastPads[pad] = copy;
        }
    }

    private void ApplyVJoy(Dictionary<int, VJoyState> want)
    {
        if (_vjoy is null) return;
        // Devices used by the active bindings stay acquired even while all their inputs are idle.
        foreach (var b in _bindings)
            if (b.Output?.Target == OutputTarget.VJoy) VJoyFor(want, b.Output.Device);

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
