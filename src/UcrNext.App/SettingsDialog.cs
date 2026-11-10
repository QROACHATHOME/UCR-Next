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
        [nameof(AxisSettings.Toggle)] = "press once for on, again for off",
        [nameof(AxisSettings.PulseMs)] = "each press gives a press of exactly this long (0 = off)",
        [nameof(AxisSettings.PulseOnRelease)] = "with a pulse: fire when the button is let go",
        [nameof(AxisSettings.MergeMode)] = "Average, Greatest (larger deflection wins) or Sum",
        [nameof(AxisSettings.MinDelta)] = "pixels per 10 ms just outside the deadzone",
        [nameof(AxisSettings.MaxDelta)] = "pixels per 10 ms at full deflection",
        [nameof(AxisSettings.DefaultValue)] = "-1..1 (e.g. -1 = throttle closed, 0 = centre)",
        [nameof(AxisSettings.CameraAngle)] = "0-90 degrees; 90 swaps roll and yaw completely",
        [nameof(AxisSettings.StateName)] = "e.g. shift; other bindings can be limited to it",
        [nameof(AxisSettings.StateOnPress)] = "On, Off, Toggle or Unchanged",
        [nameof(AxisSettings.StateOnRelease)] = "On, Off, Toggle or Unchanged",
    };

    /// <summary>Labels that read better than the property name for a mode.</summary>
    private static string LabelFor(BindingMode mode, bool buttonInput, string name) => (mode, name) switch
    {
        (BindingMode.AxisMerger, "Invert") => "Invert high axis",
        (BindingMode.AxisMerger, "InvertY") => "Invert low axis",
        (BindingMode.FpvMix, "Invert") => "Invert roll",
        (BindingMode.FpvMix, "InvertY") => "Invert yaw",
        (BindingMode.FpvMix, "Component") => "Output (X roll, Y yaw)",
        (BindingMode.ReversibleThrottle, "Invert") => "Invert input",
        (BindingMode.SetState, "StateOnPress") => buttonInput ? "When pressed" : "When entering the range",
        (BindingMode.SetState, "StateOnRelease") => buttonInput ? "When released" : "When leaving the range",
        (BindingMode.SetState, "XStart") => "Range from (-1..1)",
        (BindingMode.SetState, "XEnd") => "Range to (-1..1)",
        (BindingMode.SetState, "Invert") => "Invert input",
        (_, "PulseMs") => "Pulse (ms)",
        _ => Humanize(name),
    };

    private static readonly string[] Common = { nameof(AxisSettings.Invert) };

    private static string[] Relevant(BindingMode mode, bool buttonInput, bool triggerOutput) => mode switch
    {
        BindingMode.Direct when buttonInput => new[] { "Invert", "Toggle", "PulseMs", "PulseOnRelease", "ButtonAxisPressed", "ButtonAxisReleased" },
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
        BindingMode.AxisMerger => new[] { "MergeMode", "Invert", "InvertY", "Deadzone", "Sensitivity", "NonLinearSensitivity", "ButtonThreshold" }
            .Concat(triggerOutput ? new[] { "TriggerFullRange" } : Array.Empty<string>()).ToArray(),
        BindingMode.AxisToDelta => new[] { "Invert", "Deadzone", "Sensitivity", "NonLinearSensitivity", "MinDelta", "MaxDelta" },
        BindingMode.DefaultValue => new[] { "DefaultValue" },
        BindingMode.FpvMix => new[] { "Component", "CameraAngle", "Invert", "InvertY" },
        BindingMode.ReversibleThrottle => new[] { "Invert", "InvertOutput" },
        BindingMode.SetState when buttonInput => new[] { "StateName", "StateOnPress", "StateOnRelease", "Invert" },
        BindingMode.SetState => new[] { "StateName", "XStart", "XEnd", "StateOnPress", "StateOnRelease", "Invert" },
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

        bool buttonInput = binding.Model.Input?.Kind == InputKind.Button;
        var names = Relevant(binding.Model.Mode, buttonInput, binding.Model.Output?.IsTrigger == true);
        foreach (var name in names)
        {
            var prop = typeof(AxisSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) continue;
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = LabelFor(binding.Model.Mode, buttonInput, name), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 6, 3) };
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

        // States this binding depends on (UCR plugin filters), for every mode.
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = "Only while states", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 6, 3) };
            var box = new TextBox { Text = StateCondition.Format(binding.Model.Conditions), Margin = new Thickness(0, 12, 0, 2) };
            var hint = new TextBlock
            {
                Text = "e.g. shift, !menu — works only while shift is on and menu is off (states are set by SetState bindings). Empty = always.",
                FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 12, 0, 0),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetRow(label, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1); Grid.SetRow(hint, row); Grid.SetColumn(hint, 2);
            grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(hint);
            _apply.Add(() => binding.Model.Conditions = StateCondition.Parse(box.Text));
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
        if (t == typeof(string))
        {
            var text = new TextBox { Text = (string?)prop.GetValue(s) ?? "", Margin = new Thickness(0, 2, 0, 2) };
            _apply.Add(() => prop.SetValue(s, text.Text.Trim()));
            return text;
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
