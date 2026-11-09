using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace UcrNext.App;

/// <summary>
/// Light / dark / follow-Windows appearance (WPF-UI's Fluent styles), the Windows accent colour,
/// and dark title bars.
/// </summary>
public static class Theming
{
    private static string _mode = "System";
    private static bool _watching;

    public static string Normalize(string? mode) => mode switch
    {
        "Light" or "light" => "Light",
        "Dark" or "dark" => "Dark",
        _ => "System",
    };

    public static bool IsDark { get; private set; }

    public static void Apply(string? mode)
    {
        _mode = Normalize(mode);
        IsDark = _mode switch { "Light" => false, "Dark" => true, _ => WindowsUsesDarkApps() };
        ApplicationThemeManager.Apply(IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None, updateAccent: true);
        foreach (Window w in Application.Current.Windows) UpdateTitleBar(w);

        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (_mode == "System" && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply("System"));
            };
        }
    }

    /// <summary>Call in every window's constructor: themed background/text and a matching title bar.</summary>
    public static void Prepare(Window w)
    {
        w.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");
        w.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        w.SourceInitialized += (_, _) => UpdateTitleBar(w);
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void UpdateTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 20H1+ / 11, 19 on older Windows 10 builds.
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
    }
}
