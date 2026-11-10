namespace UcrNext.Core;

/// <summary>Describes one physical input device.</summary>
public sealed record DeviceInfo(string DeviceId, string Name, string Provider, int Buttons, int Axes);

/// <summary>Something that can read the current value of physical inputs.</summary>
public interface IInputSource
{
    /// <summary>Buttons read 0/1, axes -1..1, triggers 0..1. False if the device/control is unknown.</summary>
    bool TryRead(InputRef input, out double value);
}

/// <summary>A device provider: enumerates devices and exposes their current state.</summary>
public interface IInputProvider : IInputSource
{
    string Name { get; }
    IReadOnlyList<DeviceInfo> Devices { get; }
    /// <summary>Refreshes cached state. Called from the engine thread every tick.</summary>
    void Poll();
    /// <summary>Every control on every device with its current value (used by press-to-bind).</summary>
    IEnumerable<(InputRef Input, double Value)> ReadAll();
}

/// <summary>Keyboard and mouse output (SendInput on Windows).</summary>
public interface IKeyboardMouseOutput
{
    void Key(int vk, bool down);
    void MouseButton(MouseButton button, bool down);
    void MouseMove(int dx, int dy);
    /// <summary>Scrolls by <paramref name="delta"/> (120 = one notch; positive = up / right).</summary>
    void MouseWheel(int delta, bool horizontal);
}

/// <summary>Full state of a virtual Xbox 360 pad.</summary>
public sealed class PadState : IEquatable<PadState>
{
    public bool[] Buttons { get; } = new bool[Enum.GetValues<XboxButton>().Length];
    public double[] Axes { get; } = new double[Enum.GetValues<XboxAxis>().Length];

    public bool Equals(PadState? o) => o is not null && Buttons.SequenceEqual(o.Buttons) && Axes.SequenceEqual(o.Axes);
    public override bool Equals(object? obj) => Equals(obj as PadState);
    public override int GetHashCode() => 0;

    public void CopyFrom(PadState o)
    {
        Array.Copy(o.Buttons, Buttons, Buttons.Length);
        Array.Copy(o.Axes, Axes, Axes.Length);
    }
}

/// <summary>A configured vJoy device and the controls it has.</summary>
public sealed record VJoyDeviceInfo(int Id, IReadOnlyList<VJoyAxis> Axes, int Buttons);

/// <summary>Full state of one vJoy device.</summary>
public sealed class VJoyState : IEquatable<VJoyState>
{
    public const int MaxButtons = 128;
    public bool[] Buttons { get; } = new bool[MaxButtons];
    /// <summary>-1..1 per <see cref="VJoyAxis"/>; 0 = centre.</summary>
    public double[] Axes { get; } = new double[Enum.GetValues<VJoyAxis>().Length];

    public bool Equals(VJoyState? o) => o is not null && Buttons.SequenceEqual(o.Buttons) && Axes.SequenceEqual(o.Axes);
    public override bool Equals(object? obj) => Equals(obj as VJoyState);
    public override int GetHashCode() => 0;
    public VJoyState Clone()
    {
        var c = new VJoyState();
        Array.Copy(Buttons, c.Buttons, Buttons.Length);
        Array.Copy(Axes, c.Axes, Axes.Length);
        return c;
    }
}

/// <summary>vJoy virtual joystick output.</summary>
public interface IVJoyOutput
{
    bool IsAvailable { get; }
    string Status { get; }
    IReadOnlyList<VJoyDeviceInfo> Devices { get; }
    /// <summary>Acquires the device if needed and sends the full state.</summary>
    void Update(int device, VJoyState state);
    /// <summary>Centres and releases the device.</summary>
    void Release(int device);
}

/// <summary>Virtual Xbox 360 pads 0-3 (ViGEm on Windows).</summary>
public interface IVirtualPadOutput
{
    bool IsAvailable { get; }
    string Status { get; }
    /// <summary>Plugs in virtual pad <paramref name="pad"/> if needed and sends the full state.</summary>
    void Update(int pad, PadState state);
    /// <summary>Unplugs virtual pad <paramref name="pad"/>.</summary>
    void Disconnect(int pad);
}

/// <summary>
/// Relative-movement inputs (mouse, trackball, spinner). <paramref name="sum"/> is the movement
/// since the previous engine tick and <paramref name="events"/> how many device reports made it up
/// (UCR's delta plugins work per report, so the engine can average).
/// </summary>
public interface IDeltaSource
{
    bool TryReadDelta(InputRef input, out double sum, out int events);
}
