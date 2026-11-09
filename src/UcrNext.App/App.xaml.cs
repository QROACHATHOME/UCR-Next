using System.Windows;
using System.Windows.Threading;

namespace UcrNext.App;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        string? profileDir = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--profiles") profileDir = e.Args[i + 1];

        _services = new AppServices(profileDir);
        var window = new MainWindow(new MainViewModel(_services));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "UCR-Next error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
