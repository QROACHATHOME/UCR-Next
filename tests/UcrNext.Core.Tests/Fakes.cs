using UcrNext.Core;

namespace UcrNext.Core.Tests;

internal sealed class FakeInput : IInputSource
{
    public Dictionary<InputRef, double> Values { get; } = new();

    public bool TryRead(InputRef input, out double value)
    {
        if (input.DeviceId != "fake") { value = 0; return false; }
        value = Values.TryGetValue(input, out var v) ? v : 0;
        return true;
    }
}

internal sealed class FakeKm : IKeyboardMouseOutput
{
    public List<string> Events { get; } = new();
    public void Key(int vk, bool down) => Events.Add($"key {vk} {(down ? "down" : "up")}");
    public void MouseButton(MouseButton button, bool down) => Events.Add($"mouse {button} {(down ? "down" : "up")}");
    public void MouseMove(int dx, int dy) => Events.Add($"move {dx} {dy}");
}

internal sealed class FakePad : IVirtualPadOutput
{
    public List<PadState> Updates { get; } = new();
    public int Disconnects { get; private set; }
    public bool IsAvailable => true;
    public string Status => "fake";
    public void Update(PadState state) { var c = new PadState(); c.CopyFrom(state); Updates.Add(c); }
    public void Disconnect() => Disconnects++;
}
