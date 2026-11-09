using System.Windows;
using System.Windows.Threading;
using UcrNext.Core;
using UcrNext.Devices;

namespace UcrNext.App;

/// <summary>
/// "Press to bind": snapshots every input, then captures the first control that clearly
/// changes (button pressed, stick moved past half travel, trigger pulled past half).
/// With Interception running, each key press is seen twice (per device and "any keyboard");
/// the checkbox picks which of the two is captured.
/// </summary>
public partial class BindDialog : Window
{
    private const double Threshold = 0.5;
    private const int VK_ESCAPE = 0x1B, SC_ESCAPE = 0x01;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CompositeInputSource _inputs;
    private readonly Dictionary<InputRef, double> _baseline = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTime _started = DateTime.Now;
    private readonly bool _interception;

    public InputRef? Captured { get; private set; }

    public BindDialog(CompositeInputSource inputs, bool interceptionRunning)
    {
        InitializeComponent();
        _inputs = inputs;
        // Per-device mice work through Raw Input even without Interception, so always offer the choice.
        _interception = interceptionRunning;
        foreach (var (input, value) in inputs.ReadAll()) _baseline[input] = value;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Input, (_, _) => Check(), Dispatcher);
        Closed += (_, _) => _timer.Stop();
    }

    private void Check()
    {
        var left = Timeout - (DateTime.Now - _started);
        if (left <= TimeSpan.Zero) { DialogResult = false; return; }
        CountdownText.Text = $"Waiting… {left.Seconds + 1}s";
        bool anyDevice = AnyDeviceBox.IsChecked == true;
        bool movement = MovementBox.IsChecked == true;

        foreach (var (input, value) in _inputs.ReadAll())
        {
            if (input.Kind == InputKind.Delta)
            {
                // Movement values are "since the last read", so no baseline comparison.
                if (!movement || Math.Abs(value) < 1.0) continue;
                bool perDevice = input.DeviceId.StartsWith(RawMouseProvider.Prefix);
                if (perDevice == anyDevice) continue;
                Captured = input;
                DialogResult = true;
                return;
            }

            double before = _baseline.TryGetValue(input, out var b) ? b : 0;
            bool hit = input.Kind switch
            {
                InputKind.Button => value >= Threshold && before < Threshold,
                InputKind.Trigger => value - before >= Threshold,
                _ => Math.Abs(value - before) >= Threshold,
            };
            if (!hit) continue;

            string id = input.DeviceId;
            bool hookKb = id == KeyboardMouseHookProvider.KeyboardId, hookMouse = id == KeyboardMouseHookProvider.MouseId;
            bool icptKb = id.StartsWith(InterceptionProvider.KeyboardPrefix);
            bool icptMouse = id.StartsWith(InterceptionProvider.MousePrefix) || id.StartsWith(RawMouseProvider.Prefix);
            // Wheel "buttons" (5-8) only exist on the raw provider, for any mouse as well as per device.
            if (hookMouse && input.Index >= 5) { Captured = input; DialogResult = true; return; }

            if ((hookKb && input.Index == VK_ESCAPE) || (icptKb && input.Index == SC_ESCAPE)) { DialogResult = false; return; }
            if ((hookMouse || icptMouse) && input.Index == (int)MouseButton.Left) continue;
            if (anyDevice && (icptKb || icptMouse)) continue;   // take the "any keyboard/mouse" version
            // Per-device: skip the "any" version when a per-device one exists (keyboards need Interception).
            if (!anyDevice && (hookMouse || (hookKb && _interception))) continue;

            Captured = input;
            DialogResult = true;
            return;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
