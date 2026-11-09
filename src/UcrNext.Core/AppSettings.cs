using System.Text.Json;

namespace UcrNext.Core;

/// <summary>Application-wide settings, stored in %APPDATA%\UCR-Next\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Use the Interception driver for per-device keyboard/mouse input and blocking (needs restart).</summary>
    public bool UseInterception { get; set; }
    /// <summary>Folder containing interception.dll; empty = next to UCR-Next.exe.</summary>
    public string InterceptionDllFolder { get; set; } = "";
    /// <summary>Turn mapping on as soon as the app starts.</summary>
    public bool StartMappingOnLaunch { get; set; }
    /// <summary>Show an icon in the notification area.</summary>
    public bool ShowTrayIcon { get; set; } = true;
    /// <summary>Closing the window keeps UCR-Next running in the background.</summary>
    public bool CloseToTray { get; set; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCR-Next", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
        }
        catch (JsonException) { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
