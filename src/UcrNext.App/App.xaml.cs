using System.Windows;
using System.Windows.Threading;
using UcrNext.Core;

namespace UcrNext.App;

/// <summary>
/// Startup options:
///   --background   start without showing the window (tray icon only, unless --no-tray)
///   --start        turn mapping on immediately
///   --no-tray      no notification-area icon (control it with ucrnext.exe)
///   --profiles DIR use another profile folder
/// Only one instance runs per user; a second launch asks the first one to show its window
/// (or to start mapping, with --start) and exits.
/// </summary>
public partial class App : Application, IControlHost
{
    private Mutex? _singleInstance;
    private AppServices? _services;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private CommandPipeServer? _pipe;
    private bool _startedInBackground;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;

        var args = e.Args.ToList();
        bool background = args.Contains("--background");
        bool start = args.Contains("--start");
        bool noTray = args.Contains("--no-tray");
        int pi = args.IndexOf("--profiles");
        string? profileDir = pi >= 0 && pi + 1 < args.Count ? args[pi + 1] : null;

        _singleInstance = new Mutex(true, @"Local\UcrNext-" + Environment.UserName, out bool first);
        if (!first)
        {
            if (start) CommandPipe.Send("start");
            if (!background) CommandPipe.Send("show");
            Shutdown();
            return;
        }

        _services = new AppServices(profileDir, AppSettings.DefaultPath);
        Theming.Apply(_services.Settings.Theme);
        _vm = new MainViewModel(_services);
        _services.Hooks.PanicPressed += () => Dispatcher.InvokeAsync(() =>
        {
            if (_vm.IsMapping) { _vm.IsMapping = false; _vm.Status = "Mapping stopped by Ctrl+Alt+Pause"; }
        });

        var processor = new CommandProcessor(this);
        _pipe = new CommandPipeServer(cmd => Dispatcher.Invoke(() => processor.Execute(cmd)));

        _startedInBackground = background;
        if (!noTray && (_services.Settings.ShowTrayIcon || background)) CreateTray();
        if (start || _services.Settings.StartMappingOnLaunch) _vm.IsMapping = true;
        if (!background) ShowWindow();
    }

    private void CreateTray()
    {
        if (_tray is not null || _vm is null) return;
        _tray = new TrayIcon(_vm, ShowWindow, RequestQuit);
    }

    /// <summary>Called from the settings menu when "Show tray icon" changes.</summary>
    public void ApplyTraySetting()
    {
        if (_services!.Settings.ShowTrayIcon) CreateTray();
        else if (!_startedInBackground) { _tray?.Dispose(); _tray = null; }
    }

    /// <summary>The window's close button: hide to the tray or exit.</summary>
    public bool ShouldHideOnClose => !_quitting && _tray is not null && (_services!.Settings.CloseToTray || _startedInBackground);

    // ---- IControlHost (called on the UI thread) ----

    public bool IsMapping
    {
        get => _vm!.IsMapping;
        set => _vm!.IsMapping = value;
    }

    IReadOnlyList<Profile> IControlHost.Profiles => _vm!.Profiles.Select(p => p.Model).ToList();

    public IReadOnlyList<string> ActiveProfileNames => _services!.Engine.ActiveProfileNames;

    public void SetProfileEnabled(Profile profile, bool enabled)
    {
        var vm = _vm!.Profiles.FirstOrDefault(p => ReferenceEquals(p.Model, profile));
        if (vm is not null) vm.Enabled = enabled; // applied and saved at once
    }

    public void ReloadProfiles() => _vm!.Reload();

    public bool HasUnsavedChanges { get { _vm!.ApplyPending(); return _vm.HasUnsavedChanges; } }

    public void SaveProfiles() => _vm!.Save();

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(_vm!, this);
            _window.Closed += (_, _) => _window = null;
            MainWindow = _window;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void HideWindow() => _window?.Hide();

    public bool IsQuitting => _quitting;

    /// <summary>Exit from the window or tray: asks first if there are unsaved edits.</summary>
    public void RequestQuit()
    {
        if (_quitting || _vm is null) return;
        _vm.ApplyPending();
        if (_vm.HasUnsavedChanges)
        {
            ShowWindow();
            var answer = MessageBox.Show(_window!, "Save your changes to the profiles before closing?\n\n" +
                "Yes = save, No = throw them away, Cancel = keep UCR-Next open.",
                "UCR-Next", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) _vm.Save();
        }
        Quit();
    }

    /// <summary>Exits without asking (CLI "quit" checks for unsaved edits itself).</summary>
    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        // Let the pipe reply go out before shutting down.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _window?.Close();
            Shutdown();
        });
    }

    public IEnumerable<string> StatusLines() => _services!.StatusLines();

    public string ImportUcr(string path, bool perDevice) => _vm!.ImportUcr(path, perDevice).Report;

    protected override void OnExit(ExitEventArgs e)
    {
        _pipe?.Dispose();
        _tray?.Dispose();
        _services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "UCR-Next error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
