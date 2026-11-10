using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>
/// Up to four virtual Xbox 360 controllers and four DualShock 4s through the ViGEmBus driver,
/// plugged in on first use and unplugged when no longer needed or when UCR-Next closes.
/// (ViGEmBus also unplugs a program's pads by itself if the program crashes, so they can't be left behind.)
/// </summary>
public sealed class ViGEmPadOutput : IVirtualPadOutput, IDisposable
{
    private static readonly Xbox360Button[] Buttons =
    {
        // Order must match Core.XboxButton.
        Xbox360Button.A, Xbox360Button.B, Xbox360Button.X, Xbox360Button.Y,
        Xbox360Button.LeftShoulder, Xbox360Button.RightShoulder, Xbox360Button.Back, Xbox360Button.Start,
        Xbox360Button.Guide, Xbox360Button.LeftThumb, Xbox360Button.RightThumb,
        Xbox360Button.Up, Xbox360Button.Down, Xbox360Button.Left, Xbox360Button.Right,
    };

    private static readonly Xbox360Axis[] Sticks =
    {
        Xbox360Axis.LeftThumbX, Xbox360Axis.LeftThumbY, Xbox360Axis.RightThumbX, Xbox360Axis.RightThumbY,
    };

    private static readonly DualShock4Button[] Ds4Buttons =
    {
        // Order must match Core.Ds4Button (up to Touchpad; the D-pad is a hat).
        DualShock4Button.Cross, DualShock4Button.Circle, DualShock4Button.Square, DualShock4Button.Triangle,
        DualShock4Button.ShoulderLeft, DualShock4Button.ShoulderRight, DualShock4Button.TriggerLeft, DualShock4Button.TriggerRight,
        DualShock4Button.Share, DualShock4Button.Options, DualShock4SpecialButton.Ps, DualShock4Button.ThumbLeft,
        DualShock4Button.ThumbRight, DualShock4SpecialButton.Touchpad,
    };

    private readonly IDualShock4Controller?[] _ds4 = new IDualShock4Controller?[OutputRef.MaxXboxPads];

    private readonly object _lock = new();
    private ViGEmClient? _client;
    private readonly IXbox360Controller?[] _pads = new IXbox360Controller?[OutputRef.MaxXboxPads];
    private readonly int?[] _slots = new int?[OutputRef.MaxXboxPads];
    private readonly long[] _connectedAt = new long[OutputRef.MaxXboxPads];
    private HashSet<int>? _baseline;        // XInput slots in use before our first pad was plugged in
    private long _resolveDeadline;

    public ViGEmPadOutput()
    {
        try
        {
            _client = new ViGEmClient();
            Status = "ViGEmBus ready";
        }
        catch (Exception ex)
        {
            Status = "ViGEmBus not available (" + ex.GetType().Name + "). Install ViGEmBus to use virtual Xbox 360 pads.";
        }
    }

    public bool IsAvailable => _client is not null;
    public string Status { get; private set; }

    /// <summary>
    /// XInput slots taken by our virtual pads (hidden from inputs to avoid feedback loops).
    /// ViGEm's own UserIndex is not trusted (it was seen reporting slot 0 while the pad sat in
    /// slot 1); instead the slots that newly appear after plugging in are assigned to our pads in
    /// the order they were plugged in.
    /// </summary>
    public IReadOnlyCollection<int> ExcludedSlots
    {
        get
        {
            lock (_lock)
            {
                ResolveSlots();
                return _slots.Where(s => s is not null).Select(s => s!.Value).ToList();
            }
        }
    }

    private void ResolveSlots()
    {
        if (_baseline is null) return;
        var unresolved = Enumerable.Range(0, _pads.Length).Where(i => _pads[i] is not null && _slots[i] is null)
            .OrderBy(i => _connectedAt[i]).ToList();
        if (unresolved.Count == 0) return;

        var fresh = XInputProvider.ConnectedSlots();
        fresh.ExceptWith(_baseline);
        foreach (var s in _slots) if (s is not null) fresh.Remove(s.Value);
        if (fresh.Count >= unresolved.Count)
        {
            var ordered = fresh.OrderBy(s => s).ToList();
            for (int i = 0; i < unresolved.Count; i++) _slots[unresolved[i]] = ordered[i];
            UpdateStatus();
        }
        else if (Environment.TickCount64 > _resolveDeadline)
        {
            // Ambiguous (e.g. a real pad was plugged at the same moment): stop trying for these pads.
            foreach (var i in unresolved) _slots[i] = -1;
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        var parts = Enumerable.Range(0, _pads.Length).Where(i => _pads[i] is not null)
            .Select(i => _slots[i] is int s && s >= 0 ? $"P{i + 1}→slot {s + 1}" : $"P{i + 1}").ToList();
        var ds4 = Enumerable.Range(0, _ds4.Length).Where(i => _ds4[i] is not null).Select(i => $"#{i + 1}").ToList();
        var text = new List<string>();
        if (parts.Count > 0) text.Add("Virtual Xbox 360 pads: " + string.Join(", ", parts));
        if (ds4.Count > 0) text.Add("DualShock 4: " + string.Join(", ", ds4));
        Status = text.Count == 0 ? "ViGEmBus ready" : string.Join("; ", text);
    }

    public void Update(int pad, PadState state)
    {
        if (pad < 0 || pad >= _pads.Length) return;
        lock (_lock)
        {
            if (_client is null) return;
            var target = _pads[pad];
            if (target is null)
            {
                try
                {
                    if (_pads.All(p => p is null)) _baseline = XInputProvider.ConnectedSlots();
                    _resolveDeadline = Environment.TickCount64 + 3000;
                    target = _client.CreateXbox360Controller();
                    target.AutoSubmitReport = false;
                    target.Connect();
                    _pads[pad] = target;
                    _slots[pad] = null;
                    _connectedAt[pad] = Environment.TickCount64 * 10 + pad; // stable order within one millisecond
                    UpdateStatus();
                }
                catch (Exception ex)
                {
                    Status = $"Failed to plug in virtual pad P{pad + 1}: " + ex.Message;
                    return;
                }
            }

            for (int i = 0; i < Buttons.Length; i++) target.SetButtonState(Buttons[i], state.Buttons[i]);
            for (int i = 0; i < Sticks.Length; i++) target.SetAxisValue(Sticks[i], AxisProcessor.ToStick(state.Axes[i]));
            target.SetSliderValue(Xbox360Slider.LeftTrigger, AxisProcessor.ToTrigger(state.Axes[(int)XboxAxis.LeftTrigger]));
            target.SetSliderValue(Xbox360Slider.RightTrigger, AxisProcessor.ToTrigger(state.Axes[(int)XboxAxis.RightTrigger]));
            target.SubmitReport();
        }
    }

    public void Disconnect(int pad)
    {
        if (pad < 0 || pad >= _pads.Length) return;
        lock (_lock)
        {
            var target = _pads[pad];
            if (target is null) return;
            try { target.Disconnect(); } catch { /* already gone */ }
            _pads[pad] = null;
            _slots[pad] = null;
            if (_pads.All(p => p is null)) _baseline = null;
            if (_client is not null) UpdateStatus();
        }
    }

    public void UpdateDs4(int pad, PadState state)
    {
        if (pad < 0 || pad >= _ds4.Length) return;
        lock (_lock)
        {
            if (_client is null) return;
            var target = _ds4[pad];
            if (target is null)
            {
                try
                {
                    target = _client.CreateDualShock4Controller();
                    target.AutoSubmitReport = false;
                    target.Connect();
                    _ds4[pad] = target;
                    UpdateStatus();
                }
                catch (Exception ex)
                {
                    Status = $"Failed to plug in virtual DualShock 4 #{pad + 1}: " + ex.Message;
                    return;
                }
            }

            for (int i = 0; i < Ds4Buttons.Length; i++) target.SetButtonState(Ds4Buttons[i], state.Buttons[i]);
            int b = (int)Ds4Button.DPadUp;
            target.SetDPadDirection(DPad(state.Buttons[b], state.Buttons[b + 1], state.Buttons[b + 2], state.Buttons[b + 3]));
            // DualShock 4 sticks are bytes with 128 = centre and 0 = up/left.
            target.SetAxisValue(DualShock4Axis.LeftThumbX, ToByte(state.Axes[(int)XboxAxis.LeftX]));
            target.SetAxisValue(DualShock4Axis.LeftThumbY, ToByte(-state.Axes[(int)XboxAxis.LeftY]));
            target.SetAxisValue(DualShock4Axis.RightThumbX, ToByte(state.Axes[(int)XboxAxis.RightX]));
            target.SetAxisValue(DualShock4Axis.RightThumbY, ToByte(-state.Axes[(int)XboxAxis.RightY]));
            target.SetSliderValue(DualShock4Slider.LeftTrigger, AxisProcessor.ToTrigger(state.Axes[(int)XboxAxis.LeftTrigger]));
            target.SetSliderValue(DualShock4Slider.RightTrigger, AxisProcessor.ToTrigger(state.Axes[(int)XboxAxis.RightTrigger]));
            target.SubmitReport();
        }
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round((Math.Clamp(v, -1, 1) + 1) * 127.5), 0, 255);

    private static DualShock4DPadDirection DPad(bool up, bool down, bool left, bool right)
    {
        int y = (up ? 1 : 0) - (down ? 1 : 0), x = (right ? 1 : 0) - (left ? 1 : 0);
        return (x, y) switch
        {
            (0, 1) => DualShock4DPadDirection.North, (1, 1) => DualShock4DPadDirection.Northeast,
            (1, 0) => DualShock4DPadDirection.East, (1, -1) => DualShock4DPadDirection.Southeast,
            (0, -1) => DualShock4DPadDirection.South, (-1, -1) => DualShock4DPadDirection.Southwest,
            (-1, 0) => DualShock4DPadDirection.West, (-1, 1) => DualShock4DPadDirection.Northwest,
            _ => DualShock4DPadDirection.None,
        };
    }

    public void DisconnectDs4(int pad)
    {
        if (pad < 0 || pad >= _ds4.Length) return;
        lock (_lock)
        {
            var target = _ds4[pad];
            if (target is null) return;
            try { target.Disconnect(); } catch { /* already gone */ }
            _ds4[pad] = null;
            if (_client is not null) UpdateStatus();
        }
    }

    public void Dispose()
    {
        for (int i = _ds4.Length - 1; i >= 0; i--) DisconnectDs4(i);
        for (int i = _pads.Length - 1; i >= 0; i--) Disconnect(i);
        _client?.Dispose();
        _client = null;
    }
}
