using System.ComponentModel;
using Forms = System.Windows.Forms;

namespace UcrNext.App;

/// <summary>Notification-area icon: double-click shows the window; right-click menu for mapping/exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _mapping;
    private readonly MainViewModel _vm;

    public TrayIcon(MainViewModel vm, Action show, Action quit)
    {
        _vm = vm;
        _mapping = new Forms.ToolStripMenuItem("Mapping enabled", null, (_, _) => _vm.IsMapping = !_vm.IsMapping);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Show UCR-Next", null, (_, _) => show()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_mapping);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => quit()));

        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon.Create(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();
        _vm.PropertyChanged += OnVmChanged;
        Refresh();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsMapping)) Refresh();
    }

    private void Refresh()
    {
        _mapping.Checked = _vm.IsMapping;
        _icon.Text = _vm.IsMapping ? "UCR-Next — mapping ON" : "UCR-Next — mapping off";
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>The app icon (Assets\UCR-Next.ico, also the exe's icon), for window title bars and the tray.</summary>
internal static class AppIcon
{
    private static readonly Uri Source = new("pack://application:,,,/Assets/UCR-Next.ico");

    /// <summary>For window title bars (null if the resource can't be read).</summary>
    public static System.Windows.Media.ImageSource? Image { get; } = LoadImage();

    private static System.Windows.Media.ImageSource? LoadImage()
    {
        try { return System.Windows.Media.Imaging.BitmapFrame.Create(Source); }
        catch { return null; }
    }

    /// <summary>The tray icon at the system's small-icon size; falls back to a drawn gamepad.</summary>
    public static System.Drawing.Icon Create()
    {
        try
        {
            using var stream = System.Windows.Application.GetResourceStream(Source)!.Stream;
            return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch { return Drawn(); }
    }

    private static System.Drawing.Icon Drawn()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var body = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x2B, 0x6C, 0xD8));
            g.FillEllipse(body, 1, 8, 16, 18);
            g.FillEllipse(body, 15, 8, 16, 18);
            g.FillRectangle(body, 8, 8, 16, 12);
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            g.FillRectangle(white, 6, 14, 8, 2);
            g.FillRectangle(white, 9, 11, 2, 8);
            g.FillEllipse(white, 21, 11, 3, 3);
            g.FillEllipse(white, 24, 15, 3, 3);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
