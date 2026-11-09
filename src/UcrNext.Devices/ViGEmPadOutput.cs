using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>Up to four virtual Xbox 360 controllers through the ViGEmBus driver, plugged in on first use.</summary>
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
        Status = parts.Count == 0 ? "ViGEmBus ready" : "Virtual Xbox 360 pads: " + string.Join(", ", parts);
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

    public void Dispose()
    {
        for (int i = _pads.Length - 1; i >= 0; i--) Disconnect(i);
        _client?.Dispose();
        _client = null;
    }
}
