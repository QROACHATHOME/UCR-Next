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
    /// <summary>Updates sent to pad 0 (P1).</summary>
    public List<PadState> Updates { get; } = new();
    /// <summary>Every update / disconnect in order, e.g. "update 1", "disconnect 0".</summary>
    public List<string> Log { get; } = new();
    public Dictionary<int, PadState> Last { get; } = new();
    public int Disconnects { get; private set; }
    public bool IsAvailable => true;
    public string Status => "fake";
    public void Update(int pad, PadState state)
    {
        var c = new PadState(); c.CopyFrom(state);
        if (pad == 0) Updates.Add(c);
        Last[pad] = c;
        Log.Add("update " + pad);
    }
    public void Disconnect(int pad) { Disconnects++; Log.Add("disconnect " + pad); }
}

internal sealed class FakeDelta : IInputSource, IDeltaSource
{
    public Dictionary<InputRef, double> Values { get; } = new();
    public Dictionary<InputRef, (double Sum, int Events)> Deltas { get; } = new();

    public bool TryRead(InputRef input, out double value)
    {
        if (input.DeviceId != "fake") { value = 0; return false; }
        value = Values.TryGetValue(input, out var v) ? v : 0;
        return true;
    }

    public bool TryReadDelta(InputRef input, out double sum, out int events)
    {
        (sum, events) = Deltas.TryGetValue(input, out var d) ? d : (0, 0);
        return input.DeviceId == "fake";
    }
}
