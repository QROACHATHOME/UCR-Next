using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace UcrNext.App;

/// <summary>Rename a device, add notes ("left side, player 1") and a picture so you can recognise it.</summary>
public sealed class DeviceDialog : Window
{
    private readonly DeviceVm _device;
    private readonly DeviceCatalog _catalog;
    private readonly TextBox _name, _notes;
    private readonly CheckBox _hidden;
    private readonly Border _preview;
    private readonly Button _removePicture;
    private DeviceCatalog.PictureChange _picture = DeviceCatalog.PictureChange.None;
    private bool _hasPicture;

    public DeviceDialog(DeviceVm device, DeviceCatalog catalog)
    {
        _device = device; _catalog = catalog;
        Theming.Prepare(this);
        Title = "Device — " + device.DisplayName;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var custom = catalog.Get(device.DeviceId);

        // Picture on the left
        _preview = new Border
        {
            Width = 128, Height = 128, CornerRadius = new CornerRadius(10), ClipToBounds = true, Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Click to choose a picture",
        };
        _preview.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        _preview.MouseLeftButtonUp += (_, _) => ChoosePicture();
        ShowPreview(device.Image);

        var choose = new Button { Content = "Choose picture…", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        choose.Click += (_, _) => ChoosePicture();
        var paste = new Button { Content = "Paste picture", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0),
            ToolTip = "Use a picture copied to the clipboard (e.g. from a web page or a screenshot)" };
        paste.Click += (_, _) => PastePicture();
        _removePicture = new Button { Content = "Remove picture", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 0) };
        _removePicture.Click += (_, _) => { _picture = new DeviceCatalog.PictureChange(Remove: true); ShowPreview(null); };
        _hasPicture = device.HasImage;
        _removePicture.IsEnabled = _hasPicture;

        var left = new StackPanel { Width = 128, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(_preview);
        left.Children.Add(choose);
        left.Children.Add(paste);
        left.Children.Add(_removePicture);

        // Fields on the right
        _name = new Wpf.Ui.Controls.TextBox { Text = custom?.Name ?? "", Margin = new Thickness(0, 4, 0, 0), PlaceholderText = device.Info.Name };
        _notes = new TextBox
        {
            Text = custom?.Notes ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 80,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0),
        };
        _hidden = new CheckBox { Content = "Hide this device from the list", IsChecked = custom?.Hidden == true, Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "For devices you never bind (e.g. virtual ones). Hidden devices still work; 'Show hidden devices' lists them again." };

        var right = new StackPanel();
        right.Children.Add(Label("Your name for it"));
        right.Children.Add(_name);
        right.Children.Add(Hint($"Leave empty to use the name it reports: {device.Info.Name}"));
        right.Children.Add(Label("Notes", top: 10));
        right.Children.Add(_notes);
        right.Children.Add(_hidden);
        right.Children.Add(Hint($"{device.Info.Provider} · {device.DeviceId}", top: 10));
        right.Children.Add(Hint("Tip: press something on the device — its row in the list lights up.", top: 2));

        var body = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(right);

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            try { _catalog.Update(_device.DeviceId, _name.Text, _notes.Text, _hidden.IsChecked == true, _picture); DialogResult = true; }
            catch (Exception ex) { System.Windows.MessageBox.Show(this, "Could not save the picture: " + ex.Message, "UCR-Next"); }
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0) };
        var reset = new Button { Content = "Forget all", ToolTip = "Remove your name, notes and picture for this device" };
        reset.Click += (_, _) => { _catalog.Reset(_device.DeviceId); DialogResult = true; };

        var buttons = new DockPanel { Margin = new Thickness(16, 0, 16, 16), LastChildFill = false };
        DockPanel.SetDock(reset, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private static TextBlock Label(string text, double top = 0) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 0) };

    private static TextBlock Hint(string text, double top = 4)
    {
        var t = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return t;
    }

    private void ShowPreview(ImageSource? image)
    {
        _hasPicture = image is not null;
        if (_removePicture is not null) _removePicture.IsEnabled = _hasPicture;
        if (image is not null)
        {
            _preview.Child = new System.Windows.Controls.Image { Source = image, Stretch = Stretch.UniformToFill };
            return;
        }
        var icon = new SymbolIcon { Symbol = _device.Icon, FontSize = 48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var hint = new TextBlock { Text = "Click to add a picture", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(hint);
        _preview.Child = stack;
    }

    private void ChoosePicture()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a picture of " + _device.DisplayName,
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        var img = DeviceCatalog.TryLoad(dlg.FileName);
        if (img is null) { System.Windows.MessageBox.Show(this, "That file isn't a picture UCR-Next can show.", "UCR-Next"); return; }
        _picture = new DeviceCatalog.PictureChange(File: dlg.FileName);
        ShowPreview(img);
    }

    private void PastePicture()
    {
        BitmapSource? bmp = null;
        try { if (Clipboard.ContainsImage()) bmp = Clipboard.GetImage(); } catch { }
        if (bmp is null) { System.Windows.MessageBox.Show(this, "There's no picture on the clipboard.", "UCR-Next"); return; }
        bmp.Freeze();
        _picture = new DeviceCatalog.PictureChange(Bitmap: bmp);
        ShowPreview(bmp);
    }
}
