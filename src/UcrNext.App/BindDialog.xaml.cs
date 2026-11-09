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

    public InputRef? Captured { get; private set; }

    public BindDialog(CompositeInputSource inputs, bool interceptionRunning)
    {
        InitializeComponent();
        _inputs = inputs;
        AnyDeviceBox.Visibility = interceptionRunning ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (input, value) in inputs.ReadAll()) _baseline[input] = value;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Input, (_, _) => Check(), Dispatcher);
        Closed += (_, _) => _timer.Stop();
    }

    private void Check()
    {
        var left = Timeout - (DateTime.Now - _started);
        if (left <= TimeSpan.Zero) { DialogResult = false; return; }
        CountdownText.Text = $"Waiting… {left.Seconds + 1}s";
        bool anyDevice = AnyDeviceBox.Visibility != Visibility.Visible || AnyDeviceBox.IsChecked == true;

        foreach (var (input, value) in _inputs.ReadAll())
        {
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
            bool icptKb = id.StartsWith(InterceptionProvider.KeyboardPrefix), icptMouse = id.StartsWith(InterceptionProvider.MousePrefix);

            if ((hookKb && input.Index == VK_ESCAPE) || (icptKb && input.Index == SC_ESCAPE)) { DialogResult = false; return; }
            if ((hookMouse || icptMouse) && input.Index == (int)MouseButton.Left) continue;
            if (anyDevice && (icptKb || icptMouse)) continue;   // take the "any keyboard/mouse" version
            if (!anyDevice && (hookKb || hookMouse)) continue;  // take the per-device version

            Captured = input;
            DialogResult = true;
            return;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
