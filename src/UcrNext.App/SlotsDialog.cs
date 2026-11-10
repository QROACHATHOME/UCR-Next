using System.Windows;
using System.Windows.Controls;
using UcrNext.Core;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace UcrNext.App;

/// <summary>
/// Device slots: a name ("P1 stick") that bindings read, filled by a device you pick here, plus
/// optional extra devices for players 2-4 (UCR shadow devices).
/// </summary>
public sealed class SlotsDialog : Window
{
    private sealed record Choice(DeviceMatch? Match, string Display)
    {
        public override string ToString() => Display;
    }

    private readonly IReadOnlyList<DeviceInfo> _devices;
    private readonly Func<string, string> _label;
    private readonly StackPanel _list = new();
    private readonly List<(TextBox Name, ComboBox Main, List<ComboBox> Players, string Original)> _rows = new();
    private readonly CheckBox _convert;

    public List<DeviceSlot> Result { get; } = new();
    /// <summary>Old name → new name of renamed slots (so bindings can follow).</summary>
    public Dictionary<string, string> Renames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ConvertBindings => _convert.IsChecked == true;

    public SlotsDialog(IEnumerable<DeviceSlot> slots, IReadOnlyList<DeviceInfo> devices, Func<string, string> label)
    {
        _devices = devices.Where(d => d.DeviceId.Contains(':')).ToList();
        _label = label;
        Theming.Prepare(this);
        Title = "Device slots";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "A slot is a name your bindings read instead of one exact controller. Pick which controller fills it here: " +
                   "when a controller is replaced or gets a new number, change it once here instead of in every binding. " +
                   "When you bind a control of a slot's controller, the binding uses the slot.\n" +
                   "Extra players (UCR's shadow devices): every binding that reads the slot also runs for each extra controller, " +
                   "driving the next virtual pad or vJoy device (player 2 → #2, …) — but only while that controller is connected, " +
                   "so no virtual pad is plugged in for a controller that isn't there.",
        };

        foreach (var s in slots) AddRow(s);

        var add = new Button { Content = "Add slot", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        add.Click += (_, _) => AddRow(new DeviceSlot { Name = NewName() });

        _convert = new CheckBox
        {
            Content = "Also switch existing bindings that read these controllers to their slot (all profiles)",
            IsChecked = true, Margin = new Thickness(0, 12, 0, 0),
        };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0) };
        ok.Click += (_, _) => { if (Collect()) DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(intro);
        root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(add);
        root.Children.Add(_convert);
        root.Children.Add(buttons);
        Content = root;
    }

    private string NewName()
    {
        for (int i = 1; ; i++)
        {
            string n = $"P{i} controller";
            if (_rows.All(r => !string.Equals(r.Name.Text.Trim(), n, StringComparison.OrdinalIgnoreCase))) return n;
        }
    }

    private ComboBox DeviceBox(DeviceMatch? current, string emptyText)
    {
        var items = new List<Choice> { new(null, emptyText) };
        items.AddRange(_devices.Select(d => new Choice(new DeviceMatch { DeviceId = d.DeviceId, Name = d.Name }, _label(d.DeviceId))));
        Choice? selected = items[0];
        if (current is not null && (current.DeviceId.Length > 0 || current.Name.Length > 0))
        {
            selected = items.Skip(1).FirstOrDefault(c => string.Equals(c.Match!.DeviceId, current.DeviceId, StringComparison.OrdinalIgnoreCase))
                    ?? items.Skip(1).FirstOrDefault(c => current.Name.Length > 0 && string.Equals(c.Match!.Name, current.Name, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                selected = new Choice(current, $"{(current.Name.Length > 0 ? current.Name : current.DeviceId)} (not connected)");
                items.Insert(1, selected);
            }
        }
        var box = new ComboBox { ItemsSource = items, SelectedItem = selected, Margin = new Thickness(0, 2, 0, 2) };
        return box;
    }

    private void AddRow(DeviceSlot slot)
    {
        var name = new TextBox { Text = slot.Name, Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Top };
        var main = DeviceBox(slot.Device, "(no controller)");
        var players = new List<ComboBox>();
        var playersPanel = new StackPanel();

        void AddPlayer(DeviceMatch? m)
        {
            if (players.Count >= OutputRef.MaxXboxPads - 1) return;
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
            var label = new TextBlock { Text = $"Player {players.Count + 2}", Width = 70, VerticalAlignment = VerticalAlignment.Center };
            var box = DeviceBox(m, "(none)");
            DockPanel.SetDock(label, Dock.Left);
            line.Children.Add(label);
            line.Children.Add(box);
            players.Add(box);
            playersPanel.Children.Add(line);
        }
        foreach (var m in slot.Shadows) AddPlayer(m);

        var addPlayer = new Button { Content = "Add player", Margin = new Thickness(0, 2, 0, 0) };
        addPlayer.Click += (_, _) => AddPlayer(null);
        var remove = new Button { Content = "Remove", Margin = new Thickness(6, 2, 0, 0) };

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mid = new StackPanel();
        mid.Children.Add(main);
        mid.Children.Add(playersPanel);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        right.Children.Add(addPlayer);
        right.Children.Add(remove);
        Grid.SetColumn(mid, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(name);
        grid.Children.Add(mid);
        grid.Children.Add(right);

        var row = (name, main, players, slot.Name);
        _rows.Add(row);
        _list.Children.Add(grid);
        remove.Click += (_, _) => { _rows.Remove(row); _list.Children.Remove(grid); };
    }

    private bool Collect()
    {
        Result.Clear();
        Renames.Clear();
        foreach (var (nameBox, main, players, original) in _rows)
        {
            string name = nameBox.Text.Trim();
            if (name.Length > 0 && original.Length > 0 && !string.Equals(original, name, StringComparison.Ordinal)) Renames[original] = name;
            if (name.Length == 0) continue;
            if (Result.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                System.Windows.MessageBox.Show(this, $"There are two slots called \"{name}\".", "Device slots");
                return false;
            }
            var slot = new DeviceSlot { Name = name, Device = (main.SelectedItem as Choice)?.Match ?? new DeviceMatch() };
            foreach (var p in players)
                if ((p.SelectedItem as Choice)?.Match is { } m) slot.Shadows.Add(m);
            Result.Add(slot);
        }
        return true;
    }
}
