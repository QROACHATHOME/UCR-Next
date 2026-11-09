using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
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
        Theming.Prepare(this);
        _vm = vm;
        _app = app;
        DataContext = vm;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Selected)) AnimateProfileSwitch(); };
    }

    /// <summary>Slides the bindings in when another profile is selected.</summary>
    private void AnimateProfileSwitch()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BindingsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        if (BindingsPanel.RenderTransform is TranslateTransform t)
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.ApplyPending();
        if (_app.IsQuitting) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_app.ShouldHideOnClose)
        {
            Hide(); // unsaved edits stay active and unsaved; they are asked about when UCR-Next exits
            _vm.Status = "UCR-Next keeps running in the background (tray icon).";
            return;
        }
        _app.RequestQuit();
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

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.RequestQuit();

    private void OpenProfiles_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_vm.Services.Store.Directory) { UseShellExecute = true });

    private void TraySetting_Click(object sender, RoutedEventArgs e) => _app.ApplyTraySetting();

    private void AutostartOff_Click(object sender, RoutedEventArgs e) => _vm.SetAutostart(Autostart.Mode.Off);
    private void AutostartLogon_Click(object sender, RoutedEventArgs e) => _vm.SetAutostart(Autostart.Mode.Logon);
    private void AutostartElevated_Click(object sender, RoutedEventArgs e) => _vm.SetAutostart(Autostart.Mode.Elevated);

    private void ThemeSystem_Click(object sender, RoutedEventArgs e) => _vm.Theme = "System";
    private void ThemeLight_Click(object sender, RoutedEventArgs e) => _vm.Theme = "Light";
    private void ThemeDark_Click(object sender, RoutedEventArgs e) => _vm.Theme = "Dark";

    private void CliHelp_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "ucrnext.exe (next to UCR-Next.exe) controls the running app:\n\n" + CommandProcessor.Help +
            "\n\nAlso: ucrnext launch [--background] [--start]\n      ucrnext autostart [status|off|logon|elevated]",
            "UCR-Next command line");

    private void Details_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, _vm.DetailsText, "UCR-Next status");

    // ---- Profiles ----

    private void AddProfile_Click(object sender, RoutedEventArgs e) => _vm.AddProfile();

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is { } p) _vm.DuplicateProfile(p);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } p) return;
        if (MessageBox.Show(this, $"Delete profile \"{p.Name}\"?\n\nYou can undo this (Ctrl+Z) until you save. After saving, the file is kept in the profiles folder's \"deleted\" subfolder.",
                "UCR-Next", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.DeleteProfile(p);
    }

    private void CopyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } p) return;
        if (SetClipboard(_vm.ProfileText(p))) _vm.Status = $"Copied profile \"{p.Name}\" — paste it with Ctrl+V (here, or into a message to share it)";
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => _vm.Status = _vm.Paste(GetClipboard());

    private void ProfileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // typing in a text box
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.C when ctrl: CopyProfile_Click(sender, e); break;
            case Key.V when ctrl: Paste_Click(sender, e); break;
            case Key.D when ctrl: DuplicateProfile_Click(sender, e); break;
            case Key.Delete: DeleteProfile_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } p) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export profile", FileName = SafeFileName(p.Name) + ".ucrnext.json",
            Filter = "UCR-Next profile (*.ucrnext.json)|*.ucrnext.json|JSON files (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, _vm.ProfileText(p));
        _vm.Status = $"Exported \"{p.Name}\" to {dlg.FileName}";
    }

    private void ImportProfileFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a UCR-Next profile",
            Filter = "UCR-Next profile (*.json)|*.json|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.Status = _vm.Paste(File.ReadAllText(dlg.FileName)); }
        catch (IOException ex) { _vm.Status = "Could not read the file: " + ex.Message; }
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private bool SetClipboard(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (Exception ex) { _vm.Status = "Clipboard busy, try again: " + ex.Message; return false; }
    }

    private string? GetClipboard()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch { return null; }
    }

    // ---- Profile settings ----

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

        string how = _vm.InterceptionRunning
            ? "Yes = per device through Interception (can also block keys)"
            : "Yes = per device through Raw Input (no driver; the original key still reaches the game)";
        bool perDevice = MessageBox.Show(this,
            "Keep UCR's per-keyboard / per-mouse bindings tied to the specific device?\n\n" +
            how + "\nNo = accept them from any keyboard / mouse (can block keys)",
            "Import UCR profiles", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        try
        {
            var result = _vm.ImportUcr(dlg.FileName, perDevice);
            ShowText("UCR import report", result.Report +
                $"\n\nImported {result.Profiles.Count} profile(s). They start disabled — tick the ones you want, " +
                "or use 'ucrnext solo <profile>'. Re-importing the same file updates them and keeps your games, programs, groups and device rules.");
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
            Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Top,
        };
        var w = new Window { Title = title, Owner = this, Width = 760, Height = 480, Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Theming.Prepare(w);
        w.ShowDialog();
    }

    // ---- Groups ----

    private void AddGroup_Click(object sender, RoutedEventArgs e) => _vm.Selected?.AddGroup();

    private void AutoGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is { } p) _vm.GroupByDevice(p);
    }

    private void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupVm g) g.IsExpanded = !g.IsExpanded;
    }

    private void AddBindingToGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupVm g) return;
        g.IsExpanded = true;
        BindInput(g.Owner.AddBinding(g));
    }

    private void GroupColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not GroupVm g) return;
        var panel = new WrapPanel { Width = 5 * 30, Margin = new Thickness(4) };
        var menu = new ContextMenu { PlacementTarget = el };
        foreach (var hex in GroupColors.Palette)
        {
            var dot = new Ellipse
            {
                Width = 22, Height = 22, Margin = new Thickness(4), Fill = GroupColors.Brush(hex), Cursor = Cursors.Hand,
                Stroke = hex.Equals(g.Color, StringComparison.OrdinalIgnoreCase) ? (Brush)FindResource("TextFillColorPrimaryBrush") : null,
                StrokeThickness = 2, ToolTip = hex,
            };
            dot.MouseLeftButtonUp += (_, _) => { g.Color = hex; menu.IsOpen = false; };
            panel.Children.Add(dot);
        }
        menu.Items.Add(new MenuItem { Header = panel, StaysOpenOnClick = true });
        menu.IsOpen = true;
    }

    private void GroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not GroupVm g) return;
        var p = g.Owner;
        var menu = new ContextMenu { PlacementTarget = el };
        Add(menu, "Add binding to this group", () => { g.IsExpanded = true; BindInput(p.AddBinding(g)); });
        Add(menu, "Copy this group's bindings", () =>
        {
            if (SetClipboard(_vm.BindingsText(g.Bindings))) _vm.Status = $"Copied {g.Bindings.Count} binding(s) — paste them into any profile";
        }, g.Bindings.Count > 0);
        Add(menu, "Paste bindings into this group", () => _vm.Status = _vm.Paste(GetClipboard(), g));
        if (g.IsGroup)
        {
            menu.Items.Add(new Separator());
            int i = p.Model.Groups.IndexOf(g.Model!);
            Add(menu, "Move group up", () => p.MoveGroup(g, -1), i > 0);
            Add(menu, "Move group down", () => p.MoveGroup(g, +1), i < p.Model.Groups.Count - 1);
            menu.Items.Add(new Separator());
            Add(menu, "Delete group (keeps its bindings)", () => p.RemoveGroup(g));
        }
        menu.IsOpen = true;
    }

    private static void Add(ContextMenu menu, string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    // ---- Bindings ----

    private void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } p) return;
        BindInput(p.AddBinding());
    }

    private void DeleteBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BindingVm b) b.Owner.RemoveBinding(b);
    }

    private void BindingMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not BindingVm b) return;
        var menu = new ContextMenu { PlacementTarget = el };
        Add(menu, "Copy", () => { if (SetClipboard(_vm.BindingsText(new[] { b }))) _vm.Status = $"Copied \"{b.Name}\" — paste it into any profile"; });
        Add(menu, "Duplicate", () => b.Owner.DuplicateBinding(b));
        var move = new MenuItem { Header = "Move to group" };
        foreach (var g in b.Owner.GroupChoices)
        {
            var target = g;
            var item = new MenuItem
            {
                Header = g.Name, IsChecked = ReferenceEquals(g, b.Group),
                Icon = new Ellipse { Width = 10, Height = 10, Fill = g.Brush },
            };
            item.Click += (_, _) => b.Group = target;
            move.Items.Add(item);
        }
        var newGroup = new MenuItem { Header = "New group…" };
        newGroup.Click += (_, _) => b.Group = b.Owner.AddGroup();
        move.Items.Add(new Separator());
        move.Items.Add(newGroup);
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        Add(menu, "Delete", () => b.Owner.RemoveBinding(b));
        menu.IsOpen = true;
    }

    private void BindInput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BindingVm b) BindInput(b);
    }

    private void BindExtra_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BindingVm.ExtraSlot slot) return;
        var dlg = new BindDialog(_vm.Services.Inputs, _vm.InterceptionRunning, _vm.Services.Hooks.DrainDiagnostics) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Captured is { } input) slot.Owner.SetExtra(slot.Index, input);
    }

    private void BindingSettings_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BindingVm b) new SettingsDialog(b) { Owner = this }.ShowDialog();
    }

    private void BindInput(BindingVm binding)
    {
        var dlg = new BindDialog(_vm.Services.Inputs, _vm.InterceptionRunning, _vm.Services.Hooks.DrainDiagnostics) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Captured is { } input) binding.SetInput(input);
    }
}
