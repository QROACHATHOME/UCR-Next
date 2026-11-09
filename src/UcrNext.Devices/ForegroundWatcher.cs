using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UcrNext.Devices;

/// <summary>Reports the executable name of the foreground window's process ("game.exe"), polled 4x a second.</summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly Timer _timer;
    private IntPtr _lastHwnd;
    private uint _lastPid;
    private string? _current;

    /// <summary>Raised on a thread-pool thread when the foreground program changes.</summary>
    public event Action<string?>? Changed;

    public string? Current => _current;

    public ForegroundWatcher()
    {
        _timer = new Timer(_ => Check(), null, 0, 250);
    }

    private void Check()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == _lastHwnd) return;
            _lastHwnd = hwnd;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _lastPid) return;
            _lastPid = pid;
            string? name = ProcessName(pid);
            if (name == _current) return;
            _current = name;
            Changed?.Invoke(name);
        }
        catch { /* keep polling */ }
    }

    public static string? ProcessName(uint pid)
    {
        if (pid == 0) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch { return null; }
    }

    /// <summary>Executable names of running programs that have a visible main window.</summary>
    public static IReadOnlyList<string> RunningWindowedApps()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) set.Add(p.ProcessName + ".exe"); }
            catch { }
            finally { p.Dispose(); }
        }
        return set.ToList();
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
