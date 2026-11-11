using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace UcrNext.Devices;

/// <summary>
/// A stable key for one physical USB device, so identical devices (two spinners, two sticks)
/// never swap. Windows gives a USB device an instance id made from its serial number when it has
/// one (the same in every port), otherwise from the hub and port it is plugged into (the same as
/// long as it stays in that port). The key is that serial, or a short hash of the port-based id,
/// plus the interface / collection number for devices that show up as several mice or keyboards.
/// </summary>
public static class DeviceKeys
{
    public readonly record struct Key(string Value, bool FromSerial);

    private static readonly Dictionary<string, Key> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key for a device interface path such as <c>\\?\HID#VID_1241&amp;PID_1111#7&amp;2a&amp;0&amp;0000#{guid}</c>.</summary>
    public static Key For(string devicePath)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(devicePath, out var k)) return k;
            k = Compute(devicePath);
            Cache[devicePath] = k;
            return k;
        }
    }

    private static Key Compute(string path)
    {
        string suffix = "";
        var mi = Regex.Match(path, @"&MI_([0-9A-F]{2})", RegexOptions.IgnoreCase);
        if (mi.Success) suffix += "." + mi.Groups[1].Value.ToUpperInvariant();
        var col = Regex.Match(path, @"&COL([0-9A-F]{2})", RegexOptions.IgnoreCase);
        if (col.Success) suffix += "." + col.Groups[1].Value.ToUpperInvariant();

        string instanceId = InstanceIdFromPath(path);
        string? usb = UsbDeviceAncestor(instanceId);
        if (usb is not null)
        {
            string last = usb[(usb.LastIndexOf('\\') + 1)..];
            // Port-generated instance ids look like "6&2a1b3c4d&0&3"; a serial number has no '&'.
            if (!last.Contains('&') && last.Length > 0)
                return new Key(Clean(last) + suffix, true);
            return new Key("P" + Hash(usb) + suffix, false);
        }
        return new Key("P" + Hash(instanceId.Length > 0 ? instanceId : path) + suffix, false);
    }

    private static string Clean(string serial)
    {
        var sb = new StringBuilder();
        foreach (char c in serial) if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        var s = sb.ToString();
        return s.Length > 24 ? s[..24] : s;
    }

    public static string Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s.ToUpperInvariant()) { h ^= c; h *= 16777619; }
        return h.ToString("X8");
    }

    /// <summary><c>\\?\HID#VID_..&amp;MI_00#8&amp;3aff&amp;0&amp;0000#{guid}</c> → <c>HID\VID_..&amp;MI_00\8&amp;3aff&amp;0&amp;0000</c>.</summary>
    public static string InstanceIdFromPath(string path)
    {
        var p = path;
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\??\")) p = p[4..];
        int g = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (g >= 0) p = p[..g];
        return p.Replace('#', '\\');
    }

    /// <summary>The nearest ancestor that is a whole USB device (not one interface of it), or null.</summary>
    private static string? UsbDeviceAncestor(string instanceId)
    {
        try
        {
            if (instanceId.Length == 0 || CM_Locate_DevNodeW(out uint node, instanceId, 0) != 0) return null;
            for (int level = 0; level < 5; level++)
            {
                string? id = DeviceId(node);
                if (id is null) return null;
                if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                    return id;
                if (CM_Get_Parent(out uint parent, node, 0) != 0) return null;
                node = parent;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return null;
    }

    private static string? DeviceId(uint node)
    {
        var sb = new StringBuilder(512);
        return CM_Get_Device_IDW(node, sb, sb.Capacity, 0) == 0 ? sb.ToString() : null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLen, uint flags);
}
