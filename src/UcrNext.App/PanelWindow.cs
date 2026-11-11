using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using UcrNext.Core;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace UcrNext.App;

/// <summary>
/// The arcade control panel as it really looks: a photo with the controls placed on it (or imported
/// from LED-Next). Each control is linked to the input it produces by pressing it, and lights up
/// on screen while it is pressed. Saved at once to %APPDATA%\UCR-Next\panel.json.
/// </summary>
public sealed class PanelWindow : Window
{
    private static readonly Brush[] PlayerBrushes =
    {
        new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), // shared
        new SolidColorBrush(Color.FromRgb(0xDC, 0x45, 0x45)),
        new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        new SolidColorBrush(Color.FromRgb(0x22, 0xA0, 0x4B)),
        new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x20)),
    };
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    private sealed class Visual
    {
        public required PanelControl Model;
        public required Grid Root;
        public required Ellipse Body;
        public required Ellipse Ring;
        public long ActiveUntil;
    }

    private readonly MainViewModel _vm;
    private readonly string _path, _images;
    private PanelLayout _layout;
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Image _photo = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Dictionary<PanelControl, Visual> _visuals = new();
    private readonly StackPanel _props = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty;
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 1, Width = 140, VerticalAlignment = VerticalAlignment.Center };
    private readonly Stack<string> _undo = new(), _redo = new();
    private readonly DispatcherTimer _live;
    private PanelControl? _selected;
    private string? _clipboard;
    private Point _dragStart;
    private (double X, double Y) _dragOrigin;
    private bool _dragging, _dragMoved;

    public PanelWindow(MainViewModel vm)
    {
        _vm = vm;
        var folder = PanelLayout.FolderFor(vm.Services.Store.Directory);
        _path = PanelLayout.PathIn(folder);
        _images = PanelLayout.ImagesIn(folder);
        _layout = PanelLayout.Load(_path);
        Theming.Prepare(this);
        Title = "Arcade panel";
        Width = 1280;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ---- toolbar
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        Button Tool(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 6, 4) };
            b.Click += (_, _) => click();
            bar.Children.Add(b);
            return b;
        }
        Tool("Import from LED-Next…", "Read the layout (positions, names, photo) from LED-Next's cabinet.json. Links you made here are kept for controls with the same id.", ImportLedNext);
        Tool("Add", "Add a control", () => Add(null));
        Tool("Duplicate", "Ctrl+D", Duplicate);
        Tool("Delete", "Delete key", Delete);
        Tool("Link all…", "Go through every control that isn't linked yet: press each one on the real panel", LinkAll);
        Tool("Set photo…", "A photo of your control panel taken straight on; drag the controls onto it", SetPhoto);
        Tool("Remove photo", "", RemovePhoto);
        var opacityBox = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 4) };
        opacityBox.Children.Add(new TextBlock { Text = "Photo opacity", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        opacityBox.Children.Add(_opacity);
        bar.Children.Add(opacityBox);
        _opacity.ValueChanged += (_, e) =>
        {
            if (Math.Abs(e.NewValue - _layout.BackgroundOpacity) < 0.001) return;
            Checkpoint();
            _layout.BackgroundOpacity = e.NewValue;
            _photo.Opacity = e.NewValue;
            Save();
        };

        // ---- panel
        _canvas.Background = Brushes.Transparent;
        _canvas.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == _canvas) Select(null); };
        _canvas.MouseMove += OnDragMove;
        _canvas.MouseLeftButtonUp += OnDragEnd;
        var board = new Border { CornerRadius = new CornerRadius(8), Child = _canvas };
        board.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        _empty = new TextBlock
        {
            Text = "No panel yet. Use “Import from LED-Next…” to bring in your layout, or “Add” to place controls yourself.",
            TextWrapping = TextWrapping.Wrap, FontSize = 15, MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var stage = new Grid();
        stage.Children.Add(new Viewbox { Child = board, Stretch = Stretch.Uniform });
        stage.Children.Add(_empty);

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = "Drag controls to move them, or use the arrow keys (Shift = 10×). Tab selects the next control. Ctrl+C / Ctrl+V copy, Ctrl+Z / Ctrl+Y undo. " +
                   "A dashed orange ring means the control isn't linked to an input yet. Controls light up white while you press them.",
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var left = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        left.Children.Add(bar);
        left.Children.Add(hint);
        left.Children.Add(stage);

        // ---- properties
        var side = new ScrollViewer { Width = 320, Margin = new Thickness(14, 0, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _props };
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(left);
        Content = root;

        PreviewKeyDown += OnKey;
        _live = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (_, _) => LiveTick(), Dispatcher);
        Closed += (_, _) => _live.Stop();

        Rebuild();
        Select(null);
        _live.Start();
    }

    // ------------------------------------------------------------------ drawing

    private void Rebuild()
    {
        _canvas.Width = _layout.Width;
        _canvas.Height = _layout.Height;
        _canvas.Children.Clear();
        _visuals.Clear();
        _photo.Width = _layout.Width;
        _photo.Height = _layout.Height;
        _photo.Opacity = _layout.BackgroundOpacity;
        _photo.Source = LoadPhoto();
        _opacity.Value = _layout.BackgroundOpacity;
        _canvas.Children.Add(_photo);
        foreach (var c in _layout.Controls) _canvas.Children.Add(MakeVisual(c).Root);
        _empty.Visibility = _layout.Controls.Count == 0 && _photo.Source is null ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is not null && !_layout.Controls.Contains(_selected)) _selected = null;
        foreach (var v in _visuals.Values) Paint(v);
    }

    private ImageSource? LoadPhoto()
    {
        if (_layout.BackgroundImage is not { Length: > 0 } file) return null;
        var path = System.IO.Path.Combine(_images, file);
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            return bmp;
        }
        catch { return null; }
    }

    private Visual MakeVisual(PanelControl c)
    {
        var body = new Ellipse { StrokeThickness = 2 };
        var ring = new Ellipse { StrokeThickness = 3, Margin = new Thickness(-5), IsHitTestVisible = false };
        string glyph = c.Kind switch
        {
            PanelControlKind.Joystick => "✥",
            PanelControlKind.Spinner => "⟳",
            PanelControlKind.Trackball => "◉",
            _ => "",
        };
        var label = new TextBlock
        {
            Text = c.Label.Length > 0 ? c.Label : glyph.Length > 0 ? glyph : c.Kind.ToString().ToUpperInvariant(),
            Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = Math.Max(11, c.Size * 0.3),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        var id = new TextBlock
        {
            Text = c.Id, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-40, 0, -40, -16), TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
        };
        id.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var grid = new Grid { Width = c.Size, Height = c.Size, Cursor = Cursors.Hand, ToolTip = c.Id };
        grid.Children.Add(ring);
        grid.Children.Add(body);
        grid.Children.Add(label);
        grid.Children.Add(id);
        Canvas.SetLeft(grid, c.X - c.Size / 2);
        Canvas.SetTop(grid, c.Y - c.Size / 2);
        var v = new Visual { Model = c, Root = grid, Body = body, Ring = ring };
        _visuals[c] = v;
        grid.MouseLeftButtonDown += (_, e) => OnDragStart(c, e);
        grid.MouseRightButtonUp += (_, e) => { Select(c); e.Handled = true; };
        return v;
    }

    private void Paint(Visual v)
    {
        var c = v.Model;
        bool active = Environment.TickCount64 < v.ActiveUntil;
        v.Body.Fill = active ? ActiveBrush : PlayerBrushes[Math.Clamp(c.Player, 0, 4)];
        v.Body.Stroke = active ? PlayerBrushes[Math.Clamp(c.Player, 0, 4)] : Brushes.Black;
        if (ReferenceEquals(c, _selected))
        {
            v.Ring.SetResourceReference(Shape.StrokeProperty, "AccentFillColorDefaultBrush");
            v.Ring.StrokeDashArray = null;
            v.Ring.Visibility = Visibility.Visible;
        }
        else if (!c.IsLinked)
        {
            v.Ring.Stroke = WarnBrush;
            v.Ring.StrokeDashArray = new DoubleCollection { 2, 2 };
            v.Ring.Visibility = Visibility.Visible;
        }
        else v.Ring.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ selection and properties

    private void Select(PanelControl? c)
    {
        _selected = c;
        foreach (var v in _visuals.Values) Paint(v);
        BuildProperties();
        Focus();
    }

    private void BuildProperties()
    {
        _props.Children.Clear();
        TextBlock Head(string t) { var h = new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) }; _props.Children.Add(h); return h; }

        int linked = _layout.Controls.Count(x => x.IsLinked);
        var summary = new TextBlock { Text = $"{_layout.Controls.Count} controls · {linked} linked", FontSize = 15, FontWeight = FontWeights.SemiBold };
        _props.Children.Add(summary);
        _props.Children.Add(_status);

        var c = _selected;
        if (c is null)
        {
            var t = new TextBlock { Text = "Click a control to edit it and link it to the input it produces.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _props.Children.Add(t);
            return;
        }

        Head("Id (the same as in LED-Next)");
        var id = new TextBox { Text = c.Id };
        id.LostFocus += (_, _) =>
        {
            var v = id.Text.Trim();
            if (v.Length == 0 || v == c.Id) { id.Text = c.Id; return; }
            if (_layout.Find(v) is not null) { _status.Text = $"Another control is already called {v}."; id.Text = c.Id; return; }
            Edit(() => c.Id = v);
        };
        _props.Children.Add(id);

        Head("Label");
        var label = new TextBox { Text = c.Label };
        label.LostFocus += (_, _) => { if (label.Text != c.Label) Edit(() => c.Label = label.Text); };
        _props.Children.Add(label);

        Head("Player (0 = shared)");
        var player = new ComboBox { ItemsSource = new[] { 0, 1, 2, 3, 4 }, SelectedItem = Math.Clamp(c.Player, 0, 4) };
        player.SelectionChanged += (_, _) => { if (player.SelectedItem is int p && p != c.Player) Edit(() => c.Player = p); };
        _props.Children.Add(player);

        Head("Kind");
        var kind = new ComboBox { ItemsSource = Enum.GetValues<PanelControlKind>(), SelectedItem = c.Kind };
        kind.SelectionChanged += (_, _) =>
        {
            if (kind.SelectedItem is not PanelControlKind k || k == c.Kind) return;
            Edit(() => { c.Kind = k; c.Links.RemoveAll(l => !PanelControl.PartsOf(k).Contains(l.Part)); });
            BuildProperties();
        };
        _props.Children.Add(kind);

        Head($"Size");
        var size = new Slider { Minimum = 20, Maximum = 200, Value = c.Size };
        size.PreviewMouseLeftButtonDown += (_, _) => Checkpoint();
        size.ValueChanged += (_, e) => { c.Size = Math.Round(e.NewValue); Rebuild(); };
        size.PreviewMouseLeftButtonUp += (_, _) => Save();
        _props.Children.Add(size);

        Head("Linked inputs");
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
            Text = c.Kind switch
            {
                PanelControlKind.Joystick => "Push the stick in each direction when asked.",
                PanelControlKind.Spinner => "Turn the spinner when asked (this links that one spinner, even if you have two of the same kind).",
                PanelControlKind.Trackball => "Roll the ball left/right for X, then up/down for Y.",
                _ => "Press the control on the real panel when asked.",
            },
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _props.Children.Add(note);
        foreach (var part in c.Parts)
        {
            var link = c.Link(part);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var linkBtn = new Button { Content = link is null ? "Link…" : "Relink…", Margin = new Thickness(6, 0, 0, 0) };
            linkBtn.Click += (_, _) => LinkPart(c, part);
            var clear = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove this link", IsEnabled = link is not null };
            clear.Click += (_, _) => { Edit(() => c.Links.RemoveAll(l => l.Part == part)); BuildProperties(); };
            DockPanel.SetDock(clear, Dock.Right);
            DockPanel.SetDock(linkBtn, Dock.Right);
            row.Children.Add(clear);
            row.Children.Add(linkBtn);
            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Text = (c.Parts.Length > 1 ? part + ": " : "") + (link is null ? "not linked" : Describe(link)),
                ToolTip = link is null ? null : $"{ProfileVm.DeviceName(link.Input.DeviceId)} · {link.Input}",
            };
            if (link is null) text.Foreground = WarnBrush;
            row.Children.Add(text);
            _props.Children.Add(row);
        }
    }

    private static string Describe(PanelLink l)
    {
        string dir = l.Input.Kind == InputKind.Axis && l.Sign != 0 ? (l.Sign > 0 ? " +" : " −") : "";
        return l.Input + dir;
    }

    // ------------------------------------------------------------------ editing

    private void Checkpoint()
    {
        _undo.Push(_layout.ToJson());
        _redo.Clear();
    }

    private void Edit(Action change)
    {
        Checkpoint();
        change();
        Save();
        Rebuild();
    }

    private void Save()
    {
        try { _layout.Save(_path); }
        catch (Exception ex) { _status.Text = "Couldn't save the panel: " + ex.Message; }
    }

    private void Restore(string json)
    {
        string? keep = _selected?.Id;
        _layout = PanelLayout.FromJson(json);
        Save();
        Rebuild();
        Select(keep is null ? null : _layout.Find(keep));
    }

    private void Add(PanelControl? template)
    {
        var c = template ?? new PanelControl { Id = _layout.NextIdAfter("P1_B0"), Label = "", Player = 1, X = _layout.Width / 2, Y = _layout.Height / 2 };
        Edit(() => _layout.Controls.Add(c));
        Select(c);
    }

    private void Duplicate()
    {
        if (_selected is not { } s) return;
        var c = s.Clone();
        c.Id = _layout.NextIdAfter(s.Id);
        c.X += s.Size + 16;
        c.Links.Clear(); // a copy is another physical control
        Add(c);
    }

    private void Delete()
    {
        if (_selected is not { } s) return;
        Edit(() => _layout.Controls.Remove(s));
        Select(null);
    }

    private void Nudge(double dx, double dy)
    {
        if (_selected is not { } s) return;
        Edit(() => { s.X = Math.Clamp(s.X + dx, 0, _layout.Width); s.Y = Math.Clamp(s.Y + dy, 0, _layout.Height); });
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: Nudge(-step, 0); break;
            case Key.Right: Nudge(step, 0); break;
            case Key.Up: Nudge(0, -step); break;
            case Key.Down: Nudge(0, step); break;
            case Key.Delete: Delete(); break;
            case Key.Tab when _layout.Controls.Count > 0:
            {
                // Tab / Shift+Tab: next / previous control.
                int i = _selected is null ? -1 : _layout.Controls.IndexOf(_selected);
                int n = _layout.Controls.Count;
                i = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? (i <= 0 ? n - 1 : i - 1) : (i + 1) % n;
                Select(_layout.Controls[i]);
                break;
            }
            case Key.D when ctrl: Duplicate(); break;
            case Key.C when ctrl && _selected is not null: _clipboard = System.Text.Json.JsonSerializer.Serialize(_selected); break;
            case Key.V when ctrl && _clipboard is not null:
            {
                var c = System.Text.Json.JsonSerializer.Deserialize<PanelControl>(_clipboard)!;
                c.Id = _layout.NextIdAfter(c.Id);
                c.X += 24; c.Y += 24;
                c.Links.Clear();
                Add(c);
                break;
            }
            case Key.Z when ctrl && _undo.Count > 0: _redo.Push(_layout.ToJson()); Restore(_undo.Pop()); break;
            case Key.Y when ctrl && _redo.Count > 0: _undo.Push(_layout.ToJson()); Restore(_redo.Pop()); break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ dragging

    private void OnDragStart(PanelControl c, MouseButtonEventArgs e)
    {
        Select(c);
        _dragging = true;
        _dragMoved = false;
        _dragStart = e.GetPosition(_canvas);
        _dragOrigin = (c.X, c.Y);
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _selected is not { } c) return;
        var p = e.GetPosition(_canvas);
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 3) return;
        if (!_dragMoved) { Checkpoint(); _dragMoved = true; }
        c.X = Math.Clamp(Math.Round(_dragOrigin.X + dx), 0, _layout.Width);
        c.Y = Math.Clamp(Math.Round(_dragOrigin.Y + dy), 0, _layout.Height);
        if (_visuals.TryGetValue(c, out var v))
        {
            Canvas.SetLeft(v.Root, c.X - c.Size / 2);
            Canvas.SetTop(v.Root, c.Y - c.Size / 2);
        }
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _canvas.ReleaseMouseCapture();
        if (_dragMoved) Save();
    }

    // ------------------------------------------------------------------ photo and import

    private void SetPhoto()
    {
        var dlg = new OpenFileDialog { Title = "Photo of your control panel", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var name = CopyPhoto(dlg.FileName);
            Edit(() =>
            {
                _layout.BackgroundImage = name;
                // Match the panel to the photo's shape the first time, so it isn't stretched.
                if (_layout.Controls.Count == 0 && LoadSize(dlg.FileName) is { } sz) { _layout.Width = 1600; _layout.Height = Math.Round(1600 * sz.H / sz.W); }
            });
        }
        catch (Exception ex) { _status.Text = "Couldn't use that picture: " + ex.Message; }
    }

    private static (double W, double H)? LoadSize(string file)
    {
        try
        {
            var d = BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (d.Frames[0].PixelWidth, d.Frames[0].PixelHeight);
        }
        catch { return null; }
    }

    private string CopyPhoto(string source)
    {
        Directory.CreateDirectory(_images);
        var name = "panel-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + System.IO.Path.GetExtension(source).ToLowerInvariant();
        File.Copy(source, System.IO.Path.Combine(_images, name), overwrite: true);
        return name;
    }

    private void RemovePhoto()
    {
        if (_layout.BackgroundImage is null) return;
        Edit(() => _layout.BackgroundImage = null);
    }

    private void ImportLedNext()
    {
        var dlg = new OpenFileDialog
        {
            Title = "LED-Next's cabinet.json (in LED-Next's config folder)",
            Filter = "LED-Next cabinet|cabinet.json|JSON files|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var (imported, photo) = PanelLayout.ImportLedNext(dlg.FileName);
            if (_layout.Controls.Count > 0 &&
                System.Windows.MessageBox.Show(this,
                    $"Replace this panel with LED-Next's ({imported.Controls.Count} controls)?\n\nLinks you made here stay with the controls that have the same id.",
                    "Import from LED-Next", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            string? copied = photo is null ? null : CopyPhoto(photo);
            Edit(() =>
            {
                _layout.MergeFrom(imported);
                if (copied is not null) _layout.BackgroundImage = copied;
            });
            Select(null);
            _status.Text = $"Imported {imported.Controls.Count} controls from LED-Next" + (copied is null ? "." : " with its panel photo.");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "Couldn't read that file: " + ex.Message, "Import from LED-Next");
        }
    }

    // ------------------------------------------------------------------ linking

    /// <summary>Asks for one part of a control on the real panel. False = cancelled.</summary>
    private bool LinkPart(PanelControl c, string part)
    {
        Select(c);
        bool movement = c.Kind is PanelControlKind.Spinner or PanelControlKind.Trackball;
        string what = c.Kind switch
        {
            PanelControlKind.Joystick => $"Push {c.Id} {part.ToLowerInvariant()}",
            PanelControlKind.Spinner => $"Turn {c.Id}",
            PanelControlKind.Trackball => part == "X" ? $"Roll {c.Id} left or right" : $"Roll {c.Id} up or down",
            _ => $"Press {c.Id}" + (c.Label.Length > 0 ? $" ({c.Label})" : ""),
        };
        var dlg = new BindDialog(_vm.Services.Inputs, _vm.InterceptionRunning, _vm.Services.Hooks.DrainDiagnostics)
        {
            Owner = this,
            Prompt = what + " on the real panel.",
            CaptureMovement = movement,
            AnyDevice = !movement, // two identical spinners must be told apart
        };
        if (dlg.ShowDialog() != true || dlg.Captured is not { } captured) return false;

        var input = _vm.UseSlot(captured);
        int sign = 0;
        if (captured.Kind == InputKind.Axis)
        {
            sign = _vm.Services.Inputs.TryRead(captured, out var v) && Math.Abs(v) > 0.2 ? Math.Sign(v)
                 : part is "Up" or "Right" ? 1 : part is "Down" or "Left" ? -1 : 0;
        }
        if (movement && captured.Kind == InputKind.Delta)
        {
            // Spinner: horizontal movement; trackball: the axis asked for.
            int index = c.Kind == PanelControlKind.Trackball && part == "Y" ? 1 : c.Kind == PanelControlKind.Trackball ? 0 : captured.Index;
            input = new InputRef(input.DeviceId, InputKind.Delta, index, input.Label);
        }
        Edit(() =>
        {
            c.Links.RemoveAll(l => l.Part == part);
            c.Links.Add(new PanelLink { Part = part, Input = input, Sign = sign });
        });
        Select(c);
        _status.Text = $"{c.Id}{(c.Parts.Length > 1 ? " " + part : "")} → {Describe(c.Link(part)!)}";
        return true;
    }

    /// <summary>Goes through every part not linked yet, player by player.</summary>
    private void LinkAll()
    {
        var todo = _layout.Controls.OrderBy(c => c.Player == 0 ? 9 : c.Player).ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .SelectMany(c => c.Parts.Where(p => c.Link(p) is null).Select(p => (c, p))).ToList();
        if (todo.Count == 0) { _status.Text = "Every control is linked. Use Relink… on a control to change one."; return; }
        int done = 0;
        foreach (var (c, p) in todo)
        {
            if (!LinkPart(c, p)) break;
            done++;
        }
        _status.Text = $"Linked {done} of {todo.Count}." + (done < todo.Count ? " Stopped — press “Link all…” to carry on." : "");
        BuildProperties();
    }

    // ------------------------------------------------------------------ live view

    /// <summary>A slot input reads the controller filling the slot.</summary>
    private InputRef Resolve(InputRef r) =>
        r.Slot is { } n && _vm.Services.Engine.SlotAssignments.TryGetValue(n, out var ids) && ids[0] is { } id
            ? new InputRef(id, r.Kind, r.Index, r.Label) : r;

    private void LiveTick()
    {
        long now = Environment.TickCount64;
        var src = _vm.Services.Inputs;
        foreach (var v in _visuals.Values)
        {
            bool wasActive = now < v.ActiveUntil;
            foreach (var l in v.Model.Links)
                if (src.TryRead(Resolve(l.Input), out var value) && PanelControl.IsActive(l, value)) { v.ActiveUntil = now + 150; break; }
            bool isActive = now < v.ActiveUntil;
            if (isActive != wasActive) Paint(v);
        }
    }
}
