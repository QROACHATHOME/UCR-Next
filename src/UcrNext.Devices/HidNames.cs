using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UcrNext.Devices;

/// <summary>
/// Reads the product / manufacturer strings a USB HID device reports about itself (e.g.
/// "I-PAC 2" by "Ultimarc"), from its device path. Opening with no access rights is enough for
/// these queries and works even for keyboards and mice that Windows holds exclusively.
/// </summary>
public static class HidNames
{
    /// <summary>"I-PAC 2 (Ultimarc)", "I-PAC 2", or null when the device doesn't say.</summary>
    public static string? Describe(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return null;
        try
        {
            using var h = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var product = Read(h, HidD_GetProductString);
            if (string.IsNullOrWhiteSpace(product)) return null;
            var maker = Read(h, HidD_GetManufacturerString);
            return string.IsNullOrWhiteSpace(maker) || product.Contains(maker, StringComparison.OrdinalIgnoreCase)
                ? product : $"{product} ({maker})";
        }
        catch (Exception) { return null; } // missing hid.dll, odd path… fall back to VID:PID
    }

    private delegate bool StringGetter(SafeFileHandle h, byte[] buffer, int length);

    private static string? Read(SafeFileHandle h, StringGetter get)
    {
        var buf = new byte[512];
        if (!get(h, buf, buf.Length)) return null;
        var s = System.Text.Encoding.Unicode.GetString(buf);
        int nul = s.IndexOf('\0');
        return (nul >= 0 ? s[..nul] : s).Trim();
    }

    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] private static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] buffer, int length);
}
