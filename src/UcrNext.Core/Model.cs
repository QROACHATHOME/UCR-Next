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
}

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
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputTarget { Keyboard, Mouse, Xbox360, VJoy }

/// <summary>Axes of a vJoy device. Order matters (used as index).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VJoyAxis { X, Y, Z, RX, RY, RZ, Slider0, Slider1 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputKind { Button, Axis }

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
public enum MouseButton { Left, Right, Middle, X1, X2 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseAxis { X, Y }

/// <summary>
/// Identifies one output control. Index meaning depends on Target/Kind:
/// Keyboard/Button = Win32 virtual-key code; Mouse/Button = <see cref="MouseButton"/>;
/// Mouse/Axis = <see cref="MouseAxis"/>; Xbox360/Button = <see cref="XboxButton"/>;
/// Xbox360/Axis = <see cref="XboxAxis"/>; VJoy/Button = button number - 1; VJoy/Axis = <see cref="VJoyAxis"/>.
/// </summary>
public sealed class OutputRef : IEquatable<OutputRef>
{
    public OutputTarget Target { get; set; }
    public OutputKind Kind { get; set; }
    public int Index { get; set; }
    /// <summary>vJoy device number (1-16). 0 for other targets.</summary>
    public int Device { get; set; }

    public OutputRef() { }
    public OutputRef(OutputTarget target, OutputKind kind, int index, int device = 0)
    {
        Target = target; Kind = kind; Index = index; Device = device;
    }

    public static OutputRef VJoy(int device, VJoyAxis a) => new(OutputTarget.VJoy, OutputKind.Axis, (int)a, device);
    public static OutputRef VJoyButton(int device, int buttonNumber) => new(OutputTarget.VJoy, OutputKind.Button, buttonNumber - 1, device);

    public static OutputRef Key(int vk) => new(OutputTarget.Keyboard, OutputKind.Button, vk);
    public static OutputRef Xbox(XboxButton b) => new(OutputTarget.Xbox360, OutputKind.Button, (int)b);
    public static OutputRef Xbox(XboxAxis a) => new(OutputTarget.Xbox360, OutputKind.Axis, (int)a);
    public static OutputRef Mouse(MouseButton b) => new(OutputTarget.Mouse, OutputKind.Button, (int)b);
    public static OutputRef Mouse(MouseAxis a) => new(OutputTarget.Mouse, OutputKind.Axis, (int)a);

    [JsonIgnore]
    public bool IsTrigger => Target == OutputTarget.Xbox360 && Kind == OutputKind.Axis &&
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
    public InputRef? Input { get; set; }
    public OutputRef? Output { get; set; }
    public AxisSettings Settings { get; set; } = new();
    /// <summary>
    /// Stop the physical input from reaching other programs while this binding is active.
    /// Works for keyboard keys and mouse buttons (all devices via hooks, or one device via Interception).
    /// </summary>
    public bool Block { get; set; }
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
    /// <summary>Free-form list of games that use this profile (shown in the UI, used by "ucrnext game").</summary>
    public List<string> Games { get; set; } = new();
    public List<Binding> Bindings { get; set; } = new();

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
