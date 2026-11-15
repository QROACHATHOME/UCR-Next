using System.Windows;
using System.Windows.Controls;
using UcrNext.Core;
using UcrNext.Devices;
using static UcrNext.Core.ProfileTemplates;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace UcrNext.App;

/// <summary>
/// New profile, step by step, for people who don't want to build bindings by hand:
/// 1. name, game type, game/program, sound; 2. what the controls should act as (Xbox pad, keyboard keys,
/// or nothing yet); 3. press each control. The result is an unsaved profile, like any new one.
/// </summary>
public sealed class ProfileWizard : Window
{
    private sealed class Row
    {
        public required Step Step;
        public Pressed? First, Second;
        public OutputRef? Output;          // a key picked instead of the default
        public TextBlock Status = new();
        public Button Clear = new();
    }

    private sealed record KeyChoice(int Vk, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly MainViewModel _vm;
    private readonly ContentControl _host = new();
    private readonly TextBlock _heading = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _stepText = new();
    private readonly Button _back = new() { Content = "Back", MinWidth = 90 };
    private readonly Button _next = new() { Content = "Next", MinWidth = 110, IsDefault = true, Margin = new Thickness(6, 0, 0, 0) };
    private int _page;

    // Page 1
    private readonly TextBox _name = new() { Text = "" };
    private readonly ComboBox _type = new() { IsEditable = true, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _games = new() { AcceptsReturn = true, MinHeight = 54, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    private readonly TextBox _apps = new();
    private readonly TextBlock _soundName = new() { Text = "(none)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private string _sound = "";

    // Page 2
    private readonly RadioButton _asXbox = new() { GroupName = "target", IsChecked = true };
    private readonly RadioButton _asKeys = new() { GroupName = "target" };
    private readonly RadioButton _asEmpty = new() { GroupName = "target" };
    private readonly ComboBox _player = new() { MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };

    // Page 3
    private readonly StackPanel _rowsPanel = new();
    private List<Row> _rows = new();
    private (Target Target, int Player) _built = ((Target)(-1), 0);

    /// <summary>The new profile (after Create).</summary>
    public Profile? Result { get; private set; }

    public ProfileWizard(MainViewModel vm)
    {
        _vm = vm;
        Theming.Prepare(this);
        Title = "New profile — wizard";
        Width = 760;
        Height = 680;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _back.Click += (_, _) => Show(_page - 1);
        _next.Click += (_, _) => Next();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        buttons.Children.Add(cancel);

        _stepText.Style = (Style)FindResource("Muted");
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        top.Children.Add(_stepText);
        top.Children.Add(_heading);

        var root = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(_host);
        Content = root;

        _type.ItemsSource = vm.GameTypeChoices;
        Show(0);
        Loaded += (_, _) => { _name.Focus(); };
    }

    // ---- Pages ----

    private void Show(int page)
    {
        _page = Math.Clamp(page, 0, 2);
        _back.IsEnabled = _page > 0;
        switch (_page)
        {
            case 0:
                _stepText.Text = "Step 1 of 3";
                _heading.Text = "Name your profile and say what it's for";
                _host.Content = _page1 ??= BuildPage1();
                _next.Content = "Next";
                break;
            case 1:
                _stepText.Text = "Step 2 of 3";
                _heading.Text = "What should your controls act as?";
                _host.Content = _page2 ??= BuildPage2();
                _next.Content = _asEmpty.IsChecked == true ? "Create profile" : "Next";
                break;
            default:
                _stepText.Text = "Step 3 of 3";
                _heading.Text = "Press each control you want to use";
                PrepareRows();
                _host.Content = _page3 ??= BuildPage3();
                _next.Content = "Create profile";
                break;
        }
    }

    private UIElement? _page1, _page2, _page3;

    private void Next()
    {
        if (_page == 0)
        {
            if (_name.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "Give the profile a name, e.g. the game or the controller it is for.", Title);
                _name.Focus();
                return;
            }
            Show(1);
        }
        else if (_page == 1 && _asEmpty.IsChecked != true) Show(2);
        else Finish();
    }

    private UIElement BuildPage1()
    {
        var p = new StackPanel();
        Field(p, "Profile name", "For example the game, or the controller it is for: \"Daytona USA\", \"Arcade stick P1\".", _name);
        Field(p, "Game type (optional)", "Racing, light gun, fighting… Pick one or type your own; the profile list can be grouped by it.", _type);
        Field(p, "Games (optional)", "The games that use it, one per line. Launchers can switch it on with: ucrnext game \"name\".", _games);

        var appRow = new DockPanel();
        var pick = new Button { Content = "Pick a running program ▾", Margin = new Thickness(8, 0, 0, 0) };
        pick.Click += (_, _) => PickProgram(pick);
        DockPanel.SetDock(pick, Dock.Right);
        appRow.Children.Add(pick);
        appRow.Children.Add(_apps);
        System.Windows.Automation.AutomationProperties.SetName(_apps, "Program");
        Field(p, "Only while this program is in front (optional)", "Leave empty to use the profile everywhere. Example: mame.exe", appRow);

        var soundRow = new StackPanel { Orientation = Orientation.Horizontal };
        var choose = new Button { Content = "Choose…" };
        choose.Click += (_, _) => ChooseSound();
        var clear = new Button { Content = "No sound", Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => { _sound = ""; _soundName.Text = "(none)"; };
        soundRow.Children.Add(_soundName);
        soundRow.Children.Add(choose);
        soundRow.Children.Add(clear);
        Field(p, "Sound when it becomes active (optional)", "A short sound (wav, mp3…) so you hear that this profile switched on.", soundRow);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildPage2()
    {
        var p = new StackPanel();
        Choice(p, _asXbox, "An Xbox 360 controller",
            "Works with most PC games and emulators. Your stick, pad or arcade panel shows up in games as an Xbox controller.\n" +
            "Needs the free ViGEmBus driver. Now: " + _vm.PadStatus);
        Choice(p, _asKeys, "Keyboard keys",
            "For MAME and older games that expect keys. Starts with MAME's default keys (arrows, Ctrl, Alt, Space…); you can change each key.");
        Choice(p, _asEmpty, "Nothing yet",
            "Create the profile with its name and game type only, and add bindings yourself.");

        var playerLabel = new TextBlock { Text = "Which player?", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) };
        p.Children.Add(playerLabel);
        p.Children.Add(_player);
        var playerHint = new TextBlock { Style = (Style)FindResource("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        p.Children.Add(playerHint);

        void Update()
        {
            bool keys = _asKeys.IsChecked == true, none = _asEmpty.IsChecked == true;
            int keep = Math.Max(0, _player.SelectedIndex);
            _player.ItemsSource = keys ? new[] { "Player 1", "Player 2" } : new[] { "Player 1", "Player 2", "Player 3", "Player 4" };
            _player.SelectedIndex = Math.Min(keep, keys ? 1 : 3);
            playerLabel.Visibility = _player.Visibility = playerHint.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
            playerHint.Text = keys ? "Player 2 uses MAME's player 2 keys (R, F, D, G, A, S…)."
                                   : "Player 1 → Xbox controller #1, player 2 → #2, and so on.";
            if (_page == 1) _next.Content = none ? "Create profile" : "Next";
        }
        _asXbox.Checked += (_, _) => Update();
        _asKeys.Checked += (_, _) => Update();
        _asEmpty.Checked += (_, _) => Update();
        Update();
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildPage3()
    {
        var p = new DockPanel();
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "Click \"Set up all\" and press each control when it is asked for — or click Set on one row. " +
                   "Skip the ones you don't have. For a stick, push it in the direction asked; " +
                   "an arcade stick's buttons work too (you'll be asked for both directions).",
        };
        var all = new Wpf.Ui.Controls.Button
        {
            Content = "Set up all, one after another", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10),
        };
        all.Click += (_, _) => SetUpAll();
        var head = new StackPanel();
        head.Children.Add(intro);
        head.Children.Add(all);
        DockPanel.SetDock(head, Dock.Top);
        p.Children.Add(head);
        p.Children.Add(new ScrollViewer { Content = _rowsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return p;
    }

    private Target SelectedTarget => _asKeys.IsChecked == true ? Target.Keyboard : _asEmpty.IsChecked == true ? Target.Empty : Target.XboxPad;
    private int SelectedPlayer => Math.Max(0, _player.SelectedIndex) + 1;

    /// <summary>(Re)builds the rows when the target or player changed; keeps them when coming back.</summary>
    private void PrepareRows()
    {
        var want = (SelectedTarget, SelectedPlayer);
        if (want == _built) return;
        _built = want;
        _rows = Steps(want.SelectedTarget, want.SelectedPlayer).Select(s => new Row { Step = s }).ToList();
        _rowsPanel.Children.Clear();
        var keys = VirtualKeys.Known.Select(k => new KeyChoice(k.Vk, k.Name)).OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var r in _rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 16, 6) }; // right: room for the scroll bar
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = r.Step.Label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(label);

            FrameworkElement output;
            if (r.Step.Output.Target == OutputTarget.Keyboard)
            {
                var box = new ComboBox { ItemsSource = keys, Margin = new Thickness(0, 0, 10, 0), ToolTip = "The key it presses" };
                box.SelectedItem = keys.FirstOrDefault(k => k.Vk == r.Step.Output.Index);
                var row = r;
                box.SelectionChanged += (_, _) => { if (box.SelectedItem is KeyChoice k) row.Output = OutputRef.Key(k.Vk); };
                output = box;
            }
            else output = new TextBlock { Text = "→ " + OutputNames.Short(r.Step.Output), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("Muted") };
            Grid.SetColumn(output, 1);
            grid.Children.Add(output);

            r.Status.VerticalAlignment = VerticalAlignment.Center;
            r.Status.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(r.Status, 2);
            grid.Children.Add(r.Status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            var set = new Button { Content = "Set" };
            System.Windows.Automation.AutomationProperties.SetName(set, "Set " + r.Step.Label);
            var current = r;
            set.Click += (_, _) => Capture(current, sequential: false);
            r.Clear.Content = "Clear";
            r.Clear.Margin = new Thickness(4, 0, 0, 0);
            r.Clear.Click += (_, _) => { current.First = current.Second = null; UpdateRow(current); };
            buttons.Children.Add(set);
            buttons.Children.Add(r.Clear);
            Grid.SetColumn(buttons, 3);
            grid.Children.Add(buttons);
            _rowsPanel.Children.Add(grid);
            UpdateRow(r);
        }
    }

    private void UpdateRow(Row r)
    {
        r.Clear.IsEnabled = r.First is not null;
        if (r.First is null)
        {
            r.Status.Text = "not set";
            r.Status.Style = (Style)FindResource("Faint");
            return;
        }
        r.Status.Style = null;
        r.Status.Text = Describe(r.First, ProfileVm.DeviceName) + (r.Second is null ? "" : "  /  " + Describe(r.Second, ProfileVm.DeviceName));
        r.Status.ToolTip = r.Status.Text;
    }

    // ---- Capturing ----

    private void SetUpAll()
    {
        foreach (var r in _rows.Where(r => r.First is null).ToList())
            if (!Capture(r, sequential: true)) break;
    }

    /// <summary>Asks for the row's control (and, for a stick set up with buttons, the opposite one). False = stop.</summary>
    private bool Capture(Row r, bool sequential)
    {
        var (first, go) = Ask(r.Step.Prompt, sequential);
        if (first is null) return go;
        Pressed? second = null;
        if (r.Step.Kind == StepKind.StickAxis && first.Input.Kind == InputKind.Button && r.Step.SecondPrompt is { } prompt2)
        {
            (second, go) = Ask(prompt2, true);
            if (!go && second is null) { r.First = first; UpdateRow(r); return false; }
        }
        r.First = first;
        r.Second = second;
        UpdateRow(r);
        return true;
    }

    private (Pressed? Pressed, bool KeepGoing) Ask(string prompt, bool showSkip)
    {
        var dlg = new BindDialog(_vm.Services.Inputs, _vm.InterceptionRunning, _vm.Services.Hooks.DrainDiagnostics)
        {
            Owner = this, Prompt = prompt, ShowSkip = showSkip,
        };
        if (dlg.ShowDialog() == true && dlg.Captured is { } input) return (new Pressed(_vm.UseSlot(input), dlg.CapturedDirection), true);
        return (null, dlg.Skipped);
    }

    // ---- Page 1 helpers ----

    private void PickProgram(Button button)
    {
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var exe in ForegroundWatcher.RunningWindowedApps())
        {
            var item = new MenuItem { Header = exe };
            item.Click += (_, _) => _apps.Text = _apps.Text.Trim().Length == 0 ? exe : _apps.Text.Trim() + ", " + exe;
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no programs with windows found)", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void ChooseSound()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Sound when the profile becomes active",
            Filter = "Sound files (*.wav;*.mp3;*.wma;*.m4a;*.aac)|*.wav;*.mp3;*.wma;*.m4a;*.aac|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        _sound = dlg.FileName;
        _soundName.Text = System.IO.Path.GetFileName(_sound);
        _vm.PlaySound(_sound);
    }

    // ---- Finish ----

    private void Finish()
    {
        var target = SelectedTarget;
        var set = target == Target.Empty ? new List<Row>() : _rows.Where(r => r.First is not null).ToList();
        if (target != Target.Empty && set.Count == 0 &&
            MessageBox.Show(this, "No controls are set yet, so the profile will have no bindings.\n\nCreate it anyway?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var p = new Profile
        {
            Name = _name.Text.Trim(),
            GameType = GameTypes.Normalize(_type.Text),
            Games = Lines(_games.Text, '\r', '\n'),
            Applications = Lines(_apps.Text, ',', ';'),
            ActivationSound = _sound,
            Enabled = true,
        };
        if (set.Count > 0)
        {
            var group = new BindingGroup
            {
                Name = target == Target.XboxPad ? $"Xbox 360 #{SelectedPlayer}" : $"Keyboard keys (player {SelectedPlayer})",
                Color = GroupColors.Palette[(SelectedPlayer - 1) % GroupColors.Palette.Length],
            };
            p.Groups.Add(group);
            foreach (var r in set)
            {
                var b = Build(r.Step, r.First!, r.Second, r.Output);
                b.GroupId = group.Id;
                p.Bindings.Add(b);
            }
        }
        Result = p;
        DialogResult = true;
    }

    private static List<string> Lines(string text, params char[] separators) =>
        text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ---- Layout helpers ----

    private void Field(Panel parent, string title, string hint, FrameworkElement control)
    {
        parent.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, parent.Children.Count == 0 ? 0 : 14, 0, 2) });
        parent.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        System.Windows.Automation.AutomationProperties.SetName(control, title);
        parent.Children.Add(control);
    }

    private void Choice(Panel parent, RadioButton radio, string title, string text)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        body.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        radio.Content = body;
        System.Windows.Automation.AutomationProperties.SetName(radio, title);
        radio.Margin = new Thickness(0, 0, 0, 12);
        radio.VerticalContentAlignment = VerticalAlignment.Top;
        parent.Children.Add(radio);
    }
}
