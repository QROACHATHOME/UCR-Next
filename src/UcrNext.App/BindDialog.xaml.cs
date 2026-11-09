using System.IO;
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

    private readonly Func<string>? _hookDiag;

    public BindDialog(CompositeInputSource inputs, bool interceptionRunning, Func<string>? hookDiagnostics = null)
    {
        _hookDiag = hookDiagnostics;
        InitializeComponent(); Theming.Prepare(this);
        _inputs = inputs;
        // Per-device mice work through Raw Input even without Interception, so always offer the choice.
        _interception = interceptionRunning;
        foreach (var (input, value) in inputs.ReadAll()) _baseline[input] = value;
        Log($"--- bind dialog opened (Interception {(interceptionRunning ? "on" : "off")}); held at start: " +
            string.Join(" ", _baseline.Where(kv => kv.Value >= Threshold && kv.Key.Kind == InputKind.Button).Select(kv => $"{kv.Key.DeviceId}/{kv.Key.Index}")));
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

        var all = _inputs.ReadAll().ToList();
        LogTick(all, anyDevice);
        // The same key can be reported twice as "any keyboard" (by Raw Input and by the hook); one is enough.
        all = all.GroupBy(x => x.Input).Select(g => (g.Key, g.Max(x => x.Value))).ToList();
        foreach (var (input, value) in all)
        {
            if (input.Kind == InputKind.Delta)
            {
                // Movement values are "since the last read", so no baseline comparison.
                if (!movement || Math.Abs(value) < 1.0) continue;
                bool perDevice = input.DeviceId.StartsWith(RawInputProvider.MousePrefix);
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
            bool icptKb = id.StartsWith(InterceptionProvider.KeyboardPrefix), icptMouse = id.StartsWith(InterceptionProvider.MousePrefix);
            bool rawKb = id.StartsWith(RawInputProvider.KeyboardPrefix), rawMouse = id.StartsWith(RawInputProvider.MousePrefix);
            bool perKb = icptKb || rawKb, perMouse = icptMouse || rawMouse;
            // Wheel "buttons" (5-8) only exist on the raw provider, for any mouse as well as per device.
            bool wheel = (hookMouse || rawMouse) && input.Index >= 5;

            if ((hookKb && input.Index == VK_ESCAPE) || (perKb && input.Index == SC_ESCAPE)) { DialogResult = false; return; }
            if ((hookMouse || perMouse) && input.Index == (int)MouseButton.Left) continue;
            if (anyDevice && (perKb || perMouse)) continue;          // take the "any keyboard/mouse" version
            if (!anyDevice && ((hookKb) || (hookMouse && !wheel))) continue; // take the per-device version
            // Per device, prefer Interception (it can also block) over Raw Input (read-only) when it runs.
            if (!anyDevice && _interception && ((rawKb) || (rawMouse && !wheel))) continue;

            Captured = input;
            Log($"captured {input.DeviceId} {input.Kind} {input.Index} ({input.Label})");
            DialogResult = true;
            return;
        }
    }

    // ---- diagnostics: %LOCALAPPDATA%\UCR-Next\bind-debug.log (last ~200 KB) ----

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCR-Next", "bind-debug.log");
    private string _lastSeen = "";

    /// <summary>Logs the pressed buttons/keys each tick, but only when that set changes.</summary>
    private void LogTick(List<(InputRef Input, double Value)> all, bool anyDevice)
    {
        var pressed = all.Where(x => x.Input.Kind == InputKind.Button && x.Value >= Threshold)
            .Select(x => $"{x.Input.DeviceId}/{x.Input.Index}" + (_baseline.TryGetValue(x.Input, out var b) && b >= Threshold ? "(held at start)" : ""))
            .ToList();
        string seen = string.Join(" ", pressed);
        if (seen == _lastSeen) return;
        _lastSeen = seen;
        Log($"anyDevice={anyDevice} pressed: {(seen.Length == 0 ? "-" : seen)}");
        if (_hookDiag is not null) Log("    hook: " + _hookDiag());
    }

    private void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 200_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { /* diagnostics must never break binding */ }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
