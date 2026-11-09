using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>Virtual Xbox 360 controller through the ViGEmBus driver. Plugged in on first use.</summary>
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
    private IXbox360Controller? _pad;
    private int? _userIndex;

    public ViGEmPadOutput()
    {
        try
        {
            _client = new ViGEmClient();
            Status = "ViGEmBus ready";
        }
        catch (Exception ex)
        {
            Status = "ViGEmBus not available (" + ex.GetType().Name + "). Install ViGEmBus to use the virtual Xbox 360 pad.";
        }
    }

    public bool IsAvailable => _client is not null;
    public string Status { get; private set; }

    /// <summary>XInput slot taken by our virtual pad, once Windows has assigned one.</summary>
    public int? UserIndex
    {
        get
        {
            lock (_lock)
            {
                if (_userIndex is null && _pad is not null)
                {
                    try { _userIndex = _pad.UserIndex; } catch { /* not assigned yet */ }
                }
                return _userIndex;
            }
        }
    }

    public void Update(PadState state)
    {
        lock (_lock)
        {
            if (_client is null) return;
            if (_pad is null)
            {
                try
                {
                    _pad = _client.CreateXbox360Controller();
                    _pad.AutoSubmitReport = false;
                    _pad.Connect();
                    Status = "Virtual Xbox 360 pad connected";
                }
                catch (Exception ex)
                {
                    _pad = null;
                    Status = "Failed to plug in virtual pad: " + ex.Message;
                    return;
                }
            }

            for (int i = 0; i < Buttons.Length; i++) _pad.SetButtonState(Buttons[i], state.Buttons[i]);
            for (int i = 0; i < Sticks.Length; i++) _pad.SetAxisValue(Sticks[i], AxisProcessor.ToStick(state.Axes[i]));
            _pad.SetSliderValue(Xbox360Slider.LeftTrigger, AxisProcessor.ToTrigger(state.Axes[(int)XboxAxis.LeftTrigger]));
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, AxisProcessor.ToTrigger(state.Axes[(int)XboxAxis.RightTrigger]));
            _pad.SubmitReport();
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            if (_pad is null) return;
            try { _pad.Disconnect(); } catch { /* already gone */ }
            _pad = null;
            _userIndex = null;
            if (_client is not null) Status = "ViGEmBus ready";
        }
    }

    public void Dispose()
    {
        Disconnect();
        _client?.Dispose();
        _client = null;
    }
}
