using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UcrNext.Core;
using UcrNext.Devices;
using Wpf.Ui.Controls;

namespace UcrNext.App;

/// <summary>
/// Your names, notes and pictures for devices (stored in settings.json; pictures are copied to
/// %APPDATA%\UCR-Next\device-images). Changes are saved at once — they aren't profile edits.
/// </summary>
public sealed class DeviceCatalog
{
    private readonly AppServices _s;
    private readonly Dictionary<string, ImageSource?> _images = new(StringComparer.OrdinalIgnoreCase);

    public DeviceCatalog(AppServices services) { _s = services; Current = this; }

    /// <summary>The app's catalog (for view models that show device names and pictures).</summary>
    public static DeviceCatalog? Current { get; private set; }

    public event Action? Changed;

    /// <summary>
    /// What you saved for the device. A device can be known by two ids (its current one and the
    /// older "VID:PID#n" one), so the other id of a connected device is tried as well.
    /// </summary>
    public DeviceCustomization? Get(string deviceId)
    {
        if (_s.Settings.Devices.TryGetValue(deviceId, out var c)) return c;
        var d = _s.Inputs.Devices.FirstOrDefault(x => x.Is(deviceId));
        if (d is null) return null;
        foreach (var other in new[] { d.DeviceId, d.AltId })
            if (other is not null && other != deviceId && _s.Settings.Devices.TryGetValue(other, out c)) return c;
        return null;
    }

    /// <summary>Your name for the device, or null.</summary>
    public string? Alias(string deviceId) => Get(deviceId)?.Name is { Length: > 0 } n ? n : null;

    public bool IsHidden(string deviceId) => Get(deviceId)?.Hidden == true;

    public ImageSource? Image(string deviceId) =>
        Get(deviceId)?.Image is { Length: > 0 } file ? LoadImage(file) : null;

    /// <summary>Saves name, notes, hidden flag and (optionally) a new picture in one go.</summary>
    public void Update(string deviceId, string name, string notes, bool hidden, PictureChange picture)
    {
        var c = Get(deviceId) ?? new DeviceCustomization();
        c.Name = name.Trim();
        c.Notes = notes.Trim();
        c.Hidden = hidden;
        if (picture.Remove || picture.File is not null || picture.Bitmap is not null)
        {
            DeleteImageFile(c.Image);
            c.Image = "";
            if (picture.File is not null) c.Image = CopyImage(deviceId, picture.File);
            else if (picture.Bitmap is not null) c.Image = SaveImage(deviceId, picture.Bitmap);
        }
        Store(deviceId, c);
    }

    public void SetHidden(string deviceId, bool hidden)
    {
        var c = Get(deviceId) ?? new DeviceCustomization();
        c.Hidden = hidden;
        Store(deviceId, c);
    }

    /// <summary>Forgets everything about the device (name, notes, picture, hidden).</summary>
    public void Reset(string deviceId)
    {
        if (Get(deviceId) is { } c) DeleteImageFile(c.Image);
        _s.Settings.Devices.Remove(deviceId);
        if (_s.Inputs.Devices.FirstOrDefault(x => x.Is(deviceId)) is { } d)
        {
            _s.Settings.Devices.Remove(d.DeviceId);
            if (d.AltId is not null) _s.Settings.Devices.Remove(d.AltId);
        }
        Save();
    }

    private void Store(string deviceId, DeviceCustomization c)
    {
        // Saved under the current id from now on; drop a copy kept under the older id.
        if (_s.Inputs.Devices.FirstOrDefault(x => x.DeviceId == deviceId)?.AltId is { } alt) _s.Settings.Devices.Remove(alt);
        if (c.IsEmpty) _s.Settings.Devices.Remove(deviceId);
        else _s.Settings.Devices[deviceId] = c;
        Save();
    }

    private void Save()
    {
        _s.SaveSettings();
        Changed?.Invoke();
    }

    // ---- pictures ----

    public sealed record PictureChange(string? File = null, BitmapSource? Bitmap = null, bool Remove = false)
    {
        public static readonly PictureChange None = new();
    }

    /// <summary>Loads a picture for display (small, decoded once, file not kept open). Null if it can't be read.</summary>
    public static BitmapSource? TryLoad(string path, int decodeWidth = 160)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; }
    }

    private ImageSource? LoadImage(string file)
    {
        if (_images.TryGetValue(file, out var img)) return img;
        var path = Path.Combine(AppSettings.DeviceImagesFolder, file);
        return _images[file] = File.Exists(path) ? TryLoad(path) : null;
    }

    private static string FileBase(string deviceId) =>
        string.Concat(deviceId.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')) + "-" + DateTime.Now.Ticks;

    private static string CopyImage(string deviceId, string source)
    {
        Directory.CreateDirectory(AppSettings.DeviceImagesFolder);
        var name = FileBase(deviceId) + Path.GetExtension(source).ToLowerInvariant();
        File.Copy(source, Path.Combine(AppSettings.DeviceImagesFolder, name), overwrite: true);
        return name;
    }

    private static string SaveImage(string deviceId, BitmapSource bitmap)
    {
        Directory.CreateDirectory(AppSettings.DeviceImagesFolder);
        var name = FileBase(deviceId) + ".png";
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(Path.Combine(AppSettings.DeviceImagesFolder, name));
        enc.Save(fs);
        return name;
    }

    /// <summary>Removes a picture this app copied into its own folder (never the user's original).</summary>
    private void DeleteImageFile(string file)
    {
        if (string.IsNullOrEmpty(file)) return;
        _images.Remove(file);
        try
        {
            var path = Path.Combine(AppSettings.DeviceImagesFolder, Path.GetFileName(file));
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { } // in use: leave it, it's only a stale copy
        catch (UnauthorizedAccessException) { }
    }

    // ---- default icons ----

    /// <summary>A type icon for devices without a picture.</summary>
    public static SymbolRegular IconFor(string deviceId)
    {
        if (deviceId.StartsWith("xinput:")) return SymbolRegular.XboxController24;
        if (deviceId == KeyboardMouseHookProvider.KeyboardId || deviceId.StartsWith(RawInputProvider.KeyboardPrefix) ||
            deviceId.StartsWith(InterceptionProvider.KeyboardPrefix)) return SymbolRegular.Keyboard24;
        if (deviceId == KeyboardMouseHookProvider.MouseId || deviceId.StartsWith(RawInputProvider.MousePrefix) ||
            deviceId.StartsWith(InterceptionProvider.MousePrefix) || deviceId == RawInputProvider.AnyMouse) return SymbolRegular.CursorClick24;
        return SymbolRegular.Joystick20;
    }
}

/// <summary>One row of the "Detected devices" list.</summary>
public sealed class DeviceVm : Observable
{
    private readonly DeviceCatalog _catalog;
    private bool _isActive;

    public DeviceVm(DeviceInfo info, DeviceCatalog catalog) { Info = info; _catalog = catalog; }

    public DeviceInfo Info { get; private set; }
    public string DeviceId => Info.DeviceId;

    public string? Alias => _catalog.Alias(DeviceId);
    public string DisplayName => Alias ?? Info.Name;
    /// <summary>Under the name: the reported name when renamed, otherwise how it is connected.</summary>
    public string Subtitle => Alias is not null ? Info.Name : $"{Info.Provider} · {DeviceId}";
    public string Notes => _catalog.Get(DeviceId)?.Notes ?? "";
    public bool HasNotes => Notes.Length > 0;
    public ImageSource? Image => _catalog.Image(DeviceId);
    public bool HasImage => Image is not null;
    public bool HasNoImage => !HasImage;
    public SymbolRegular Icon => DeviceCatalog.IconFor(DeviceId);
    public bool IsHidden => _catalog.IsHidden(DeviceId);
    public double RowOpacity => IsHidden ? 0.5 : 1;

    public string ToolTip =>
        $"{DisplayName}\n{(Alias is not null ? "Reports itself as: " + Info.Name + "\n" : "")}" +
        $"{Info.Provider} · {DeviceId}" + (HasNotes ? "\n\n" + Notes : "") +
        "\n\nDouble-click to rename it, add notes or a picture. Press something on it and it lights up here.";

    /// <summary>True briefly after the device was used.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    internal long ActiveUntil;

    public void Update(DeviceInfo info)
    {
        if (info == Info) return;
        Info = info;
        Refresh();
    }

    public void Refresh()
    {
        foreach (var n in new[] { nameof(Alias), nameof(DisplayName), nameof(Subtitle), nameof(Notes), nameof(HasNotes), nameof(Image),
                     nameof(HasImage), nameof(HasNoImage), nameof(IsHidden), nameof(RowOpacity), nameof(ToolTip) })
            Raise(n);
    }
}
