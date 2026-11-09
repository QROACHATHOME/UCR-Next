using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using UcrNext.Core;

namespace UcrNext.App;

/// <summary>
/// Edits a binding's <see cref="AxisSettings"/>, showing only the settings that matter for its
/// mode. Built in code from the property list so new settings appear automatically.
/// </summary>
public sealed class SettingsDialog : Window
{
    private static readonly Dictionary<string, string> Help = new()
    {
        [nameof(AxisSettings.Deadzone)] = "0-0.95 of the travel ignored around centre",
        [nameof(AxisSettings.AntiDeadzone)] = "0-0.95: smallest output once outside the deadzone",
        [nameof(AxisSettings.Sensitivity)] = "1 = unchanged (UCR 100 %)",
        [nameof(AxisSettings.NonLinearSensitivity)] = "UCR's curve instead of a plain multiplier",
        [nameof(AxisSettings.ButtonThreshold)] = "axis value at which a button output counts as pressed",
        [nameof(AxisSettings.ButtonAxisPressed)] = "axis value while the button is held (-1..1)",
        [nameof(AxisSettings.ButtonAxisReleased)] = "axis value while released (-1..1)",
        [nameof(AxisSettings.Half)] = "axis splitter: use only the High or Low half",
        [nameof(AxisSettings.Component)] = "stick modes: X or Y output; long press: X = short, Y = long",
        [nameof(AxisSettings.RotationDegrees)] = "rotate the stick (-360..360)",
        [nameof(AxisSettings.DeltaSensitivity)] = "UCR 'Sensitivity (multiplier)'",
        [nameof(AxisSettings.DeltaDeadzone)] = "ignore movements smaller than this many counts",
        [nameof(AxisSettings.TimeoutMs)] = "ms without movement before returning to centre / releasing",
        [nameof(AxisSettings.Direction)] = "+1 = fires on positive movement, -1 = negative",
        [nameof(AxisSettings.IncrementRate)] = "UCR 'Sensitivity %' of Axis to Axis Increment",
    };

    private static readonly string[] Common = { nameof(AxisSettings.Invert) };

    private static string[] Relevant(BindingMode mode, bool buttonInput, bool triggerOutput) => mode switch
    {
        BindingMode.Direct when buttonInput => new[] { "Invert", "ButtonAxisPressed", "ButtonAxisReleased" },
        BindingMode.Direct => new[] { "Invert", "Deadzone", "AntiDeadzone", "Sensitivity", "NonLinearSensitivity", "ButtonThreshold", "Half" }
            .Concat(triggerOutput ? new[] { "TriggerFullRange" } : Array.Empty<string>()).ToArray(),
        BindingMode.ButtonsToAxis => new[] { "Invert" },
        BindingMode.StickPair => new[] { "Component", "Invert", "InvertY", "Deadzone", "CircularDeadzone", "Sensitivity", "NonLinearSensitivity", "RotationDegrees" },
        BindingMode.StickTrim => new[] { "Component", "Invert", "InvertY", "Deadzone", "CircularDeadzone", "Sensitivity", "NonLinearSensitivity" },
        BindingMode.AxesRange => new[] { "Invert", "InvertY", "XStart", "XEnd", "YStart", "YEnd" },
        BindingMode.DeltaToAxis => new[] { "DeltaMode", "DeltaSensitivity", "DeltaDeadzone", "TimeoutMs", "Invert" },
        BindingMode.DeltaToButtons => new[] { "Direction", "DeltaDeadzone", "TimeoutMs", "DebounceMs" },
        BindingMode.LongPress => new[] { "Component", "LongPressMs", "ShortPressMs" },
        BindingMode.Increment => new[] { "Invert", "Deadzone", "Sensitivity", "NonLinearSensitivity", "IncrementRate", "CounterEffect" },
        _ => Common,
    };

    private readonly List<Action> _apply = new();

    public SettingsDialog(BindingVm binding)
    {
        Theming.Prepare(this);
        Title = $"Settings — {binding.Name}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var s = binding.Model.Settings;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var names = Relevant(binding.Model.Mode, binding.Model.Input?.Kind == InputKind.Button, binding.Model.Output?.IsTrigger == true);
        foreach (var name in names)
        {
            var prop = typeof(AxisSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) continue;
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = Humanize(name), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 6, 3) };
            Grid.SetRow(label, row);
            grid.Children.Add(label);

            FrameworkElement editor = MakeEditor(prop, s);
            Grid.SetRow(editor, row); Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);

            if (Help.TryGetValue(name, out var help))
            {
                var hint = new TextBlock { Text = help, FontSize = 11,
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                Grid.SetRow(hint, row); Grid.SetColumn(hint, 2);
                grid.Children.Add(hint);
            }
        }

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            foreach (var a in _apply) a();
            binding.SettingsEdited();
            DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(grid);
        Content = root;
    }

    private FrameworkElement MakeEditor(PropertyInfo prop, AxisSettings s)
    {
        var t = prop.PropertyType;
        if (t == typeof(bool))
        {
            var cb = new CheckBox { IsChecked = (bool)prop.GetValue(s)!, VerticalAlignment = VerticalAlignment.Center };
            _apply.Add(() => prop.SetValue(s, cb.IsChecked == true));
            return cb;
        }
        if (t.IsEnum)
        {
            var combo = new ComboBox { ItemsSource = Enum.GetValues(t), SelectedItem = prop.GetValue(s), Margin = new Thickness(0, 2, 0, 2) };
            _apply.Add(() => { if (combo.SelectedItem is not null) prop.SetValue(s, combo.SelectedItem); });
            return combo;
        }
        var box = new TextBox
        {
            Text = Convert.ToString(prop.GetValue(s), CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 2, 0, 2),
        };
        _apply.Add(() =>
        {
            if (t == typeof(int) && int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) prop.SetValue(s, i);
            else if (t == typeof(double) && double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) prop.SetValue(s, d);
        });
        return box;
    }

    private static string Humanize(string name) => Regex.Replace(name, "(?<=[a-z])([A-Z])", " $1").Replace("Ms", "(ms)");
}
