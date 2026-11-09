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
public enum OutputTarget { Keyboard, Mouse, Xbox360 }

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
/// Xbox360/Axis = <see cref="XboxAxis"/>.
/// </summary>
public sealed class OutputRef : IEquatable<OutputRef>
{
    public OutputTarget Target { get; set; }
    public OutputKind Kind { get; set; }
    public int Index { get; set; }

    public OutputRef() { }
    public OutputRef(OutputTarget target, OutputKind kind, int index) { Target = target; Kind = kind; Index = index; }

    public static OutputRef Key(int vk) => new(OutputTarget.Keyboard, OutputKind.Button, vk);
    public static OutputRef Xbox(XboxButton b) => new(OutputTarget.Xbox360, OutputKind.Button, (int)b);
    public static OutputRef Xbox(XboxAxis a) => new(OutputTarget.Xbox360, OutputKind.Axis, (int)a);
    public static OutputRef Mouse(MouseButton b) => new(OutputTarget.Mouse, OutputKind.Button, (int)b);
    public static OutputRef Mouse(MouseAxis a) => new(OutputTarget.Mouse, OutputKind.Axis, (int)a);

    public bool IsTrigger => Target == OutputTarget.Xbox360 && Kind == OutputKind.Axis &&
                             (Index == (int)XboxAxis.LeftTrigger || Index == (int)XboxAxis.RightTrigger);

    public bool Equals(OutputRef? o) => o is not null && Target == o.Target && Kind == o.Kind && Index == o.Index;
    public override bool Equals(object? obj) => Equals(obj as OutputRef);
    public override int GetHashCode() => HashCode.Combine(Target, Kind, Index);
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
}

public sealed class Binding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New binding";
    public bool Enabled { get; set; } = true;
    public InputRef? Input { get; set; }
    public OutputRef? Output { get; set; }
    public AxisSettings Settings { get; set; } = new();
}

public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public bool Enabled { get; set; } = true;
    public List<Binding> Bindings { get; set; } = new();
}
