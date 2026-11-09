using System.Windows;
using System.Windows.Threading;
using UcrNext.Core;
using UcrNext.Devices;

namespace UcrNext.App;

/// <summary>
/// "Press to bind": snapshots every input, then captures the first control that clearly
/// changes (button pressed, stick moved past half travel, trigger pulled past half).
/// </summary>
public partial class BindDialog : Window
{
    private const double Threshold = 0.5;
    private const int VK_ESCAPE = 0x1B;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CompositeInputSource _inputs;
    private readonly Dictionary<InputRef, double> _baseline = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTime _started = DateTime.Now;

    public InputRef? Captured { get; private set; }

    public BindDialog(CompositeInputSource inputs)
    {
        InitializeComponent();
        _inputs = inputs;
        foreach (var (input, value) in inputs.ReadAll()) _baseline[input] = value;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Input, (_, _) => Check(), Dispatcher);
        Closed += (_, _) => _timer.Stop();
    }

    private void Check()
    {
        var left = Timeout - (DateTime.Now - _started);
        if (left <= TimeSpan.Zero) { DialogResult = false; return; }
        CountdownText.Text = $"Waiting… {left.Seconds + 1}s";

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

            if (input.DeviceId == KeyboardMouseHookProvider.KeyboardId && input.Index == VK_ESCAPE) { DialogResult = false; return; }
            if (input.DeviceId == KeyboardMouseHookProvider.MouseId && input.Index == (int)MouseButton.Left) continue;

            Captured = input;
            DialogResult = true;
            return;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
