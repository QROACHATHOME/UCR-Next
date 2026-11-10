using System.Text.Json.Serialization;

namespace UcrNext.Core;

/// <summary>Kind of a physical input control.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputKind
{
    Button,
    /// <summary>Bipolar axis, value range -1..1 (sticks).</summary>
    Axis,
    /// <summary>Unipolar axis, value range 0..1 (triggers).</summary>
    Trigger,
    /// <summary>Relative movement (mouse/trackball/spinner counts); index 0 = X, 1 = Y.</summary>
    Delta,
}

/// <summary>How a binding turns its input(s) into its output. Direct = one input, one output.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BindingMode
{
    /// <summary>Input → output with deadzone/sensitivity/invert (all the basic UCR plugins).</summary>
    Direct,
    /// <summary>Two buttons → one axis: Input alone = +1, ExtraInputs[0] alone = -1, both/none = 0.</summary>
    ButtonsToAxis,
    /// <summary>
    /// Stick pair: Input = X axis, ExtraInputs[0] = Y axis; deadzone (optionally circular), sensitivity,
    /// invert X/Y, rotation; outputs the X or Y component (Settings.Component).
    /// </summary>
    StickPair,
    /// <summary>StickPair plus trim: ExtraInputs[1] = trim button (adds the current position), [2] = reset trim.</summary>
    StickTrim,
    /// <summary>Button pressed while both axes (Input, ExtraInputs[0]) are inside the configured box.</summary>
    AxesRange,
    /// <summary>Mouse/trackball delta → axis, absolute (decays after a timeout) or relative (accumulates). ExtraInputs[0] = optional reset button.</summary>
    DeltaToAxis,
    /// <summary>Mouse/trackball delta → button for one direction (Settings.Direction), with centre timeout and debounce.</summary>
    DeltaToButtons,
    /// <summary>One button → short-press or long-press output (Settings.Component = Short / Long).</summary>
    LongPress,
    /// <summary>Axis moves the output gradually ("incremental"), like UCR's Axis to Axis Increment.</summary>
    Increment,
    /// <summary>Two axes → one (average, greatest or sum): Input = high axis, ExtraInputs[0] = low axis. UCR Axis Merger.</summary>
    AxisMerger,
    /// <summary>Axis → mouse movement at Min..Max pixels per 10 ms (UCR Axis to Delta). Output: a mouse axis.</summary>
    AxisToDelta,
    /// <summary>No input: holds the output axis at Settings.DefaultValue until another binding on it moves (UCR Axis Initializer).</summary>
    DefaultValue,
    /// <summary>Roll + yaw mixed by a camera angle (UCR FPV Angle Mix): Input = roll, ExtraInputs[0] = yaw; Component X = roll out, Y = yaw out.</summary>
    FpvMix,
    /// <summary>Throttle axis → half axis; ExtraInputs[0] = button that flips between forward and reverse (UCR Reversible Throttle).</summary>
    ReversibleThrottle,
    /// <summary>No output: a button (or an axis entering/leaving a range) switches a named state on/off (UCR Button/Axis to Filter).</summary>
    SetState,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MergeMode { Average, Greatest, Sum }

/// <summary>What a <see cref="BindingMode.SetState"/> binding does to its state.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StateAction { Unchanged, On, Off, Toggle }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PairComponent { X, Y }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AxisHalf { Full, High, Low }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeltaMode { Absolute, Relative }

/// <summary>Identifies one control on one physical device, e.g. "xinput:0" button 12.</summary>
public sealed class InputRef : IEquatable<InputRef>
{
    public string DeviceId { get; set; } = "";
    public InputKind Kind { get; set; }
    public int Index { get; set; }
    /// <summary>Human-readable label captured at bind time (display only).</summary>
    public string Label { get; set; } = "";

    public InputRef() { }
    public InputRef(string deviceId, InputKind kind, int index, string label = "")
    {
        DeviceId = deviceId; Kind = kind; Index = index; Label = label;
    }

    public bool Equals(InputRef? other) =>
        other is not null && DeviceId == other.DeviceId && Kind == other.Kind && Index == other.Index;
    public override bool Equals(object? obj) => Equals(obj as InputRef);
    public override int GetHashCode() => HashCode.Combine(DeviceId, Kind, Index);
    public override string ToString() => string.IsNullOrEmpty(Label) ? $"{DeviceId} {Kind} {Index}" : Label;

    /// <summary>DeviceId prefix of an input that reads through a <see cref="DeviceSlot"/>: "slot:Player 1 stick".</summary>
    public const string SlotPrefix = "slot:";
    public static string SlotId(string slotName) => SlotPrefix + slotName;
    /// <summary>The slot name of a slot device id, or null.</summary>
    public static string? SlotOf(string deviceId) =>
        deviceId.StartsWith(SlotPrefix, StringComparison.Ordinal) ? deviceId[SlotPrefix.Length..] : null;
    [JsonIgnore] public string? Slot => SlotOf(DeviceId);
}

/// <summary>
/// A named stand-in for a physical device ("Player 1 stick"). Bindings read the slot, the slot
/// says which device fills it, so a replaced or renumbered controller is one change here.
/// <see cref="Shadows"/> are UCR's shadow devices: every binding that reads the slot also runs once
/// per extra device, with its virtual pad / vJoy output moved up by one per extra device
/// (player 2 stick → Xbox 360 #2, and so on).
/// </summary>
public sealed class DeviceSlot
{
    public string Name { get; set; } = "Slot";
    /// <summary>The device that fills the slot (matched by id, or by name if the id changed).</summary>
    public DeviceMatch Device { get; set; } = new();
    /// <summary>Extra devices: players 2, 3, … (UCR shadow devices).</summary>
    public List<DeviceMatch> Shadows { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputTarget { Keyboard, Mouse, Xbox360, VJoy, DualShock4 }

/// <summary>Axes of a vJoy device. Order matters (used as index).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VJoyAxis { X, Y, Z, RX, RY, RZ, Slider0, Slider1 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputKind
{
    Button,
    Axis,
    /// <summary>vJoy POV hat direction: Index = (hat - 1) × 4 + direction (0 up, 1 right, 2 down, 3 left).</summary>
    Pov,
}

/// <summary>Buttons of the virtual DualShock 4. Order matters (used as index).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Ds4Button
{
    Cross, Circle, Square, Triangle, L1, R1, L2, R2, Share, Options, PS, L3, R3, Touchpad,
    DPadUp, DPadDown, DPadLeft, DPadRight,
}

/// <summary>Buttons of the virtual Xbox 360 pad. Order matters (used as index).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum XboxButton
{
    A, B, X, Y, LeftShoulder, RightShoulder, Back, Start, Guide,
    LeftThumb, RightThumb, DPadUp, DPadDown, DPadLeft, DPadRight,
}

/// <summary>Axes of the virtual Xbox 360 pad. Order matters (used as index).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum XboxAxis { LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseButton
{
    Left, Right, Middle, X1, X2,
    /// <summary>Wheel "buttons": as an output, one wheel notch each time it is pressed.</summary>
    WheelUp, WheelDown, WheelRight, WheelLeft,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseAxis
{
    X, Y,
    /// <summary>Scrolls continuously: up for positive values.</summary>
    Wheel,
    /// <summary>Scrolls sideways continuously: right for positive values.</summary>
    HWheel,
}

/// <summary>
/// Identifies one output control. Index meaning depends on Target/Kind:
/// Keyboard/Button = Win32 virtual-key code; Mouse/Button = <see cref="MouseButton"/>;
/// Mouse/Axis = <see cref="MouseAxis"/>; Xbox360/Button = <see cref="XboxButton"/>;
/// Xbox360/Axis = <see cref="XboxAxis"/>; VJoy/Button = button number - 1; VJoy/Axis = <see cref="VJoyAxis"/>;
/// VJoy/Pov = (hat - 1) × 4 + direction; DualShock4/Button = <see cref="Ds4Button"/>;
/// DualShock4/Axis = <see cref="XboxAxis"/> (sticks, then L2 / R2 analog).
/// </summary>
public sealed class OutputRef : IEquatable<OutputRef>
{
    public OutputTarget Target { get; set; }
    public OutputKind Kind { get; set; }
    public int Index { get; set; }
    /// <summary>vJoy device number (1-16), or virtual Xbox 360 / DualShock 4 pad number 0-3 (#1-#4). 0 for other targets.</summary>
    public int Device { get; set; }

    public const int MaxXboxPads = 4;

    public OutputRef() { }
    public OutputRef(OutputTarget target, OutputKind kind, int index, int device = 0)
    {
        Target = target; Kind = kind; Index = index; Device = device;
    }

    public static OutputRef VJoy(int device, VJoyAxis a) => new(OutputTarget.VJoy, OutputKind.Axis, (int)a, device);
    public static OutputRef VJoyButton(int device, int buttonNumber) => new(OutputTarget.VJoy, OutputKind.Button, buttonNumber - 1, device);

    public static OutputRef Key(int vk) => new(OutputTarget.Keyboard, OutputKind.Button, vk);
    public static OutputRef Xbox(XboxButton b, int pad = 0) => new(OutputTarget.Xbox360, OutputKind.Button, (int)b, pad);
    public static OutputRef Xbox(XboxAxis a, int pad = 0) => new(OutputTarget.Xbox360, OutputKind.Axis, (int)a, pad);
    public static OutputRef Mouse(MouseButton b) => new(OutputTarget.Mouse, OutputKind.Button, (int)b);
    public static OutputRef Mouse(MouseAxis a) => new(OutputTarget.Mouse, OutputKind.Axis, (int)a);
    public static OutputRef Ds4(Ds4Button b, int pad = 0) => new(OutputTarget.DualShock4, OutputKind.Button, (int)b, pad);
    public static OutputRef Ds4(XboxAxis a, int pad = 0) => new(OutputTarget.DualShock4, OutputKind.Axis, (int)a, pad);
    /// <summary>vJoy POV hat <paramref name="hat"/> (1-4), direction 0 up, 1 right, 2 down, 3 left.</summary>
    public static OutputRef VJoyPov(int device, int hat, int direction) =>
        new(OutputTarget.VJoy, OutputKind.Pov, (hat - 1) * 4 + direction, device);

    public const int MaxPovHats = 4;

    /// <summary>A virtual pad output (Xbox 360 or DualShock 4).</summary>
    [JsonIgnore]
    public bool IsPad => Target is OutputTarget.Xbox360 or OutputTarget.DualShock4;

    [JsonIgnore]
    public bool IsTrigger => IsPad && Kind == OutputKind.Axis &&
                             (Index == (int)XboxAxis.LeftTrigger || Index == (int)XboxAxis.RightTrigger);

    public bool Equals(OutputRef? o) =>
        o is not null && Target == o.Target && Kind == o.Kind && Index == o.Index && Device == o.Device;
    public override bool Equals(object? obj) => Equals(obj as OutputRef);
    public override int GetHashCode() => HashCode.Combine(Target, Kind, Index, Device);
    public override string ToString() => OutputNames.Describe(this);
}

/// <summary>Per-binding shaping of the input value.</summary>
public sealed class AxisSettings
{
    /// <summary>0..0.95 fraction of travel ignored around the center.</summary>
    public double Deadzone { get; set; }
    /// <summary>Multiplier applied after deadzone (1 = unchanged).</summary>
    public double Sensitivity { get; set; } = 1.0;
    public bool Invert { get; set; }
    /// <summary>Value at or above which an analog input counts as a pressed button.</summary>
    public double ButtonThreshold { get; set; } = 0.5;
    /// <summary>0..0.95: smallest output once the input leaves the deadzone (counters a game's own deadzone).</summary>
    public double AntiDeadzone { get; set; }
    /// <summary>Button → axis: axis value while the button is held (-1..1).</summary>
    public double ButtonAxisPressed { get; set; } = 1.0;
    /// <summary>Button → axis: axis value while the button is released (-1..1).</summary>
    public double ButtonAxisReleased { get; set; }
    /// <summary>
    /// Axis → trigger: use the whole axis travel (-1 = released, +1 = fully pulled) instead of
    /// only its positive half. Right for pedals and for sticks driving a trigger.
    /// </summary>
    public bool TriggerFullRange { get; set; }

    /// <summary>UCR's non-linear sensitivity curve (s·v + (1-s)·v³) instead of a plain multiplier.</summary>
    public bool NonLinearSensitivity { get; set; }
    /// <summary>Axis splitter: use only the positive (High) or negative (Low) half, stretched to the full range.</summary>
    public AxisHalf Half { get; set; }

    // ---- Multi-input / stateful modes (see BindingMode) ----

    /// <summary>StickPair/StickTrim: which rotated component this binding outputs; LongPress: X = short, Y = long.</summary>
    public PairComponent Component { get; set; }
    /// <summary>StickPair/StickTrim/AxesRange: invert the Y (second) axis. (Invert applies to X.)</summary>
    public bool InvertY { get; set; }
    /// <summary>StickPair/StickTrim: radial deadzone instead of per-axis.</summary>
    public bool CircularDeadzone { get; set; }
    /// <summary>StickPair: rotate the stick by this many degrees (-360..360).</summary>
    public double RotationDegrees { get; set; }
    /// <summary>AxesRange: box limits, -1..1.</summary>
    public double XStart { get; set; } = -0.5;
    public double XEnd { get; set; } = 0.5;
    public double YStart { get; set; } = -0.5;
    public double YEnd { get; set; } = 0.5;
    /// <summary>DeltaToAxis mode.</summary>
    public DeltaMode DeltaMode { get; set; }
    /// <summary>DeltaToAxis: multiplier (UCR "Sensitivity (multiplier)").</summary>
    public double DeltaSensitivity { get; set; } = 1;
    /// <summary>DeltaToAxis/DeltaToButtons: ignore movements smaller than this many counts.</summary>
    public int DeltaDeadzone { get; set; }
    /// <summary>DeltaToAxis (absolute): return to centre after this many ms without movement.
    /// DeltaToButtons: release after this many ms without movement.</summary>
    public int TimeoutMs { get; set; } = 100;
    /// <summary>DeltaToButtons: direction this binding fires for (+1 high / -1 low).</summary>
    public int Direction { get; set; } = 1;
    /// <summary>DeltaToButtons: state changes are applied after this many ms.</summary>
    public int DebounceMs { get; set; } = 100;
    /// <summary>LongPress: hold time for the long press.</summary>
    public int LongPressMs { get; set; } = 200;
    /// <summary>LongPress: how long the short-press output is held.</summary>
    public int ShortPressMs { get; set; } = 50;
    /// <summary>Increment: output change per second at full input (UCR "Sensitivity %" ×100/s).</summary>
    public double IncrementRate { get; set; } = 2;
    /// <summary>Increment: multiplier when the input moves back toward centre against the output.</summary>
    public double CounterEffect { get; set; } = 1.5;

    /// <summary>Button input: each press switches the output on, the next press off.</summary>
    public bool Toggle { get; set; }
    /// <summary>Button input: each press gives a press of exactly this many ms (0 = follow the button). UCR Event to Button.</summary>
    public int PulseMs { get; set; }
    /// <summary>With <see cref="PulseMs"/>: pulse when the button is released instead of pressed.</summary>
    public bool PulseOnRelease { get; set; }
    /// <summary>AxisMerger: how the two axes are combined.</summary>
    public MergeMode MergeMode { get; set; }
    /// <summary>AxisToDelta: mouse pixels per 10 ms just outside the deadzone and at full deflection.</summary>
    public int MinDelta { get; set; } = 1;
    public int MaxDelta { get; set; } = 20;
    /// <summary>DefaultValue: the starting axis value (-1..1).</summary>
    public double DefaultValue { get; set; }
    /// <summary>FpvMix: camera angle in degrees (0..90).</summary>
    public double CameraAngle { get; set; }
    /// <summary>ReversibleThrottle: invert the output.</summary>
    public bool InvertOutput { get; set; }
    /// <summary>SetState: the state's name (e.g. "shift"); not case-sensitive.</summary>
    public string StateName { get; set; } = "";
    /// <summary>SetState: action when the button is pressed / the axis enters XStart..XEnd.</summary>
    public StateAction StateOnPress { get; set; } = StateAction.On;
    /// <summary>SetState: action when the button is released / the axis leaves XStart..XEnd.</summary>
    public StateAction StateOnRelease { get; set; } = StateAction.Off;
}

/// <summary>A binding only works while the named state is on (or off, with <see cref="Not"/>). UCR plugin filters.</summary>
public sealed class StateCondition
{
    public string Name { get; set; } = "";
    public bool Not { get; set; }

    public override string ToString() => (Not ? "!" : "") + Name;

    /// <summary>"shift, !menu" → conditions.</summary>
    public static List<StateCondition> Parse(string? text) =>
        (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.StartsWith('!') ? new StateCondition { Name = t[1..].Trim(), Not = true } : new StateCondition { Name = t })
            .Where(c => c.Name.Length > 0).ToList();

    public static string Format(IEnumerable<StateCondition> conditions) => string.Join(", ", conditions);
}

/// <summary>Identifies a physical device for a profile rule (matches by id, or by name if the id differs).</summary>
public sealed class DeviceMatch
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";

    public bool Matches(DeviceInfo d) =>
        (!string.IsNullOrEmpty(DeviceId) && string.Equals(d.DeviceId, DeviceId, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrEmpty(Name) && string.Equals(d.Name, Name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => string.IsNullOrEmpty(Name) ? DeviceId : Name;
}

public sealed class Binding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New binding";
    public bool Enabled { get; set; } = true;
    public BindingMode Mode { get; set; }
    public InputRef? Input { get; set; }
    /// <summary>Additional inputs some modes use (see <see cref="BindingMode"/>). Null entries = not bound.</summary>
    public List<InputRef?> ExtraInputs { get; set; } = new();
    public OutputRef? Output { get; set; }
    public AxisSettings Settings { get; set; } = new();

    public InputRef? Extra(int i) => i < ExtraInputs.Count ? ExtraInputs[i] : null;

    /// <summary>Names of the extra inputs a mode uses.</summary>
    public static string[] ExtraInputNames(BindingMode mode) => mode switch
    {
        BindingMode.ButtonsToAxis => new[] { "2nd button (pushes axis negative)" },
        BindingMode.StickPair => new[] { "Y axis" },
        BindingMode.StickTrim => new[] { "Y axis", "Trim button", "Reset trim button" },
        BindingMode.AxesRange => new[] { "Axis 2" },
        BindingMode.DeltaToAxis => new[] { "Reset button (optional)" },
        BindingMode.AxisMerger => new[] { "Low axis" },
        BindingMode.FpvMix => new[] { "Yaw axis" },
        BindingMode.ReversibleThrottle => new[] { "Reverse button" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Modes that work without an input / without an output.</summary>
    public static bool NeedsInput(BindingMode mode) => mode != BindingMode.DefaultValue;
    public static bool NeedsOutput(BindingMode mode) => mode != BindingMode.SetState;

    /// <summary>Complete enough to run.</summary>
    [JsonIgnore]
    public bool IsComplete => (Input is not null || !NeedsInput(Mode)) && (Output is not null || !NeedsOutput(Mode));

    /// <summary>Only active while these states are on/off (empty = always).</summary>
    public List<StateCondition> Conditions { get; set; } = new();
    /// <summary>
    /// Stop the physical input from reaching other programs while this binding is active.
    /// Works for keyboard keys and mouse buttons (all devices via hooks, or one device via Interception).
    /// </summary>
    public bool Block { get; set; }
    /// <summary>The <see cref="BindingGroup"/> this binding is shown in; null = not in a group.</summary>
    public Guid? GroupId { get; set; }
}

/// <summary>A named, coloured group of bindings in a profile (e.g. "Player 1 pad"). Display only.</summary>
public sealed class BindingGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New group";
    /// <summary>"#RRGGBB".</summary>
    public string Color { get; set; } = "#3B82F6";
    /// <summary>Folded up in the editor.</summary>
    public bool Collapsed { get; set; }
}

public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Executable names (e.g. "game.exe"). When non-empty the profile is only active while one of
    /// these programs has the foreground window. Empty = active everywhere.
    /// </summary>
    public List<string> Applications { get; set; } = new();
    /// <summary>
    /// The profile is skipped while any of these devices is connected (e.g. skip the
    /// "pad as wheel" profile when the real wheel is plugged in), so no virtual controller is created for it.
    /// </summary>
    public List<DeviceMatch> SkipIfDevicesPresent { get; set; } = new();
    /// <summary>
    /// The profile waits until every device its bindings read is connected, so its virtual pads
    /// are never plugged in for a missing controller (and real pads keep their XInput slots).
    /// </summary>
    public bool RequireDevices { get; set; }
    /// <summary>Free-form list of games that use this profile (shown in the UI, used by "ucrnext game").</summary>
    public List<string> Games { get; set; } = new();
    public List<Binding> Bindings { get; set; } = new();
    /// <summary>Groups the bindings are organised in (in display order).</summary>
    public List<BindingGroup> Groups { get; set; } = new();

    /// <summary>True if a skip rule matches one of the connected devices.</summary>
    public static bool SkippedByDevices(IReadOnlyCollection<DeviceMatch> rules, IReadOnlyCollection<DeviceInfo> connected) =>
        rules.Count > 0 && connected.Any(d => rules.Any(r => r.Matches(d)));

    /// <summary>True if the profile applies while <paramref name="foregroundExe"/> is in front.</summary>
    public static bool AppliesTo(IReadOnlyCollection<string> applications, string? foregroundExe)
    {
        if (applications.Count == 0) return true;
        if (string.IsNullOrEmpty(foregroundExe)) return false;
        foreach (var a in applications)
            if (string.Equals(NormalizeExe(a), foregroundExe, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>"C:\Games\Game.EXE" or "game" -> "Game.EXE" / "game.exe".</summary>
    public static string NormalizeExe(string name)
    {
        var n = Path.GetFileName(name.Trim().Trim('"'));
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe";
    }
}
