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
    /// <summary>"System" (follow Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Your own name, notes and picture per device, by device id.</summary>
    public Dictionary<string, DeviceCustomization> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Also list devices marked hidden.</summary>
    public bool ShowHiddenDevices { get; set; }

    /// <summary>Folder for device pictures (copied there so the originals can move).</summary>
    public static string DeviceImagesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCR-Next", "device-images");

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UCR-Next", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
                s.Devices = new Dictionary<string, DeviceCustomization>(s.Devices ?? new(), StringComparer.OrdinalIgnoreCase);
                return s;
            }
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

/// <summary>What you told UCR-Next about one device, so you can recognise it.</summary>
public sealed class DeviceCustomization
{
    /// <summary>Your name for it ("Player 1 stick"); empty = the name the device reports.</summary>
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>File name in <see cref="AppSettings.DeviceImagesFolder"/>; empty = the default icon.</summary>
    public string Image { get; set; } = "";
    /// <summary>Left out of the device list (unless "show hidden" is on).</summary>
    public bool Hidden { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Name.Length == 0 && Notes.Length == 0 && Image.Length == 0 && !Hidden;
}
