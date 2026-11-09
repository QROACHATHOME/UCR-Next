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

/// <summary>Virtual gamepad output (ViGEm on Windows).</summary>
public interface IVirtualPadOutput
{
    bool IsAvailable { get; }
    string Status { get; }
    /// <summary>Plugs in the virtual pad if needed and sends the full state.</summary>
    void Update(PadState state);
    /// <summary>Unplugs the virtual pad.</summary>
    void Disconnect();
}
