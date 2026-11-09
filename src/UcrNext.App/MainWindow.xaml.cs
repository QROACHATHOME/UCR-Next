using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UcrNext.Core;
using UcrNext.Devices;

namespace UcrNext.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly App _app;

    public MainWindow(MainViewModel vm, App app)
    {
        InitializeComponent();
        _vm = vm;
        _app = app;
        DataContext = vm;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.Flush();
        if (_app.ShouldHideOnClose)
        {
            e.Cancel = true;
            Hide();
            _vm.Status = "UCR-Next keeps running in the background (tray icon).";
            return;
        }
        base.OnClosing(e);
        if (!_app.IsQuitting) _app.Quit();
    }

    // ---- Menu ----

    private void HideWindow_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.ShouldHideOnClose && !_vm.ShowTrayIcon)
        {
            MessageBox.Show(this, "With no tray icon, bring the window back with:\n\n    ucrnext show", "UCR-Next");
        }
        Hide();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.Quit();

    private void OpenProfiles_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_vm.Services.Store.Directory) { UseShellExecute = true });

    private void TraySetting_Click(object sender, RoutedEventArgs e) => _app.ApplyTraySetting();

    private void AutostartOff_Click(object sender, RoutedEventArgs e) => _vm.SetAutostart(Autostart.Mode.Off);
    private void AutostartLogon_Click(object sender, RoutedEventArgs e) => _vm.SetAutostart(Autostart.Mode.Logon);
    private void AutostartElevated_Click(object sender, RoutedEventArgs e) => _vm.SetAutostart(Autostart.Mode.Elevated);

    private void CliHelp_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "ucrnext.exe (next to UCR-Next.exe) controls the running app:\n\n" + CommandProcessor.Help +
            "\n\nAlso: ucrnext launch [--background] [--start]\n      ucrnext autostart [status|off|logon|elevated]",
            "UCR-Next command line");

    private void Details_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, _vm.DetailsText, "UCR-Next status");

    // ---- Profiles ----

    private void AddProfile_Click(object sender, RoutedEventArgs e) => _vm.AddProfile();

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } p) return;
        if (MessageBox.Show(this, $"Delete profile \"{p.Name}\"?", "UCR-Next", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.DeleteProfile(p);
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } profile || sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var exe in ForegroundWatcher.RunningWindowedApps())
        {
            var item = new MenuItem { Header = exe };
            item.Click += (_, _) => profile.AddApplication(exe);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no programs with windows found)", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void AddSkipDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } profile || sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var d in _vm.Devices.Where(d => d.DeviceId is not ("keyboard" or "mouse")))
        {
            var item = new MenuItem { Header = $"{d.Name}   ({d.DeviceId})" };
            item.Click += (_, _) => profile.AddSkipDevice(d);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no controllers connected — plug the device in first)", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void RemoveSkipDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is { } profile && (sender as FrameworkElement)?.DataContext is DeviceMatch m) profile.RemoveSkipDevice(m);
    }

    private void ImportUcr_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select UCR's context.xml (in the folder where UCR.exe is)",
            Filter = "UCR settings (context.xml)|context.xml|XML files (*.xml)|*.xml|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        bool perDevice = false;
        if (_vm.InterceptionRunning)
        {
            perDevice = MessageBox.Show(this,
                "Keep UCR's per-keyboard / per-mouse bindings tied to the specific device?\n\n" +
                "Yes = per device (uses Interception)\nNo = accept them from any keyboard / mouse",
                "Import UCR profiles", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        try
        {
            var result = _vm.ImportUcr(dlg.FileName, perDevice);
            ShowText("UCR import report", result.Report +
                $"\n\nImported {result.Profiles.Count} profile(s). They start disabled — tick the ones you want, " +
                "or use 'ucrnext solo <profile>'. Re-importing the same file updates them and keeps your games, programs and device rules.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not import: " + ex.Message, "Import UCR profiles", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowText(string title, string text)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(8),
        };
        new Window { Title = title, Owner = this, Width = 760, Height = 480, Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }

    // ---- Bindings ----

    private void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } p) return;
        var b = p.AddBinding();
        BindInput(b);
    }

    private void DeleteBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BindingVm b) b.Owner.RemoveBinding(b);
    }

    private void BindInput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BindingVm b) BindInput(b);
    }

    private void BindInput(BindingVm binding)
    {
        var dlg = new BindDialog(_vm.Services.Inputs, _vm.InterceptionRunning) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Captured is { } input) binding.SetInput(input);
    }
}
