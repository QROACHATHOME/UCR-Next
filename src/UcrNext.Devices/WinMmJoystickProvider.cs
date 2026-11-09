using System.Runtime.InteropServices;
using Microsoft.Win32;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>
/// Reads DirectInput/HID game controllers (joysticks, wheels, generic pads) through the
/// WinMM joystick API: up to 16 devices, 6 axes, 32 buttons and one POV hat each.
/// Xbox controllers are skipped here because <see cref="XInputProvider"/> handles them
/// (this also hides our own ViGEm virtual pad, which would otherwise feed back into itself).
/// </summary>
public sealed class WinMmJoystickProvider : IInputProvider
{
    private const int MaxButtons = 32;
    private const int PovBase = 32; // buttons 32..35 = POV up/right/down/left
    private static readonly string[] AxisNames = { "X", "Y", "Z", "R", "U", "V" };
    private static readonly string[] PovNames = { "POV Up", "POV Right", "POV Down", "POV Left" };

    // Microsoft Xbox controller product ids (handled by XInput instead).
    private static readonly HashSet<ushort> XboxPids = new()
    {
        0x028E, 0x028F, 0x0291, 0x02A1, 0x0719, 0x02D1, 0x02DD, 0x02E3, 0x02EA, 0x02FD,
        0x0B00, 0x0B05, 0x0B12, 0x0B13, 0x0B20, 0x0B22,
    };

    private sealed class Joy
    {
        public uint Id;
        public string DeviceId = "";
        public string Name = "";
        public JOYCAPSW Caps;
        public int AxisCount;
        public readonly double[] Axes = new double[6];
        public uint Buttons;
        public int Pov = -1; // hundredths of a degree, -1 = centered
    }

    private readonly object _lock = new();
    private List<Joy> _joys = new();
    private long _nextScan;
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();

    public string Name => "DirectInput / HID (WinMM)";
    public IReadOnlyList<DeviceInfo> Devices => _devices;

    public void Poll()
    {
        long now = Environment.TickCount64;
        if (now >= _nextScan) { Rescan(); _nextScan = now + 2000; }

        List<Joy> joys;
        lock (_lock) joys = _joys;
        foreach (var j in joys)
        {
            var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
            if (joyGetPosEx(j.Id, ref info) != 0) continue;
            uint[] raw = { info.dwXpos, info.dwYpos, info.dwZpos, info.dwRpos, info.dwUpos, info.dwVpos };
            (uint min, uint max)[] range =
            {
                (j.Caps.wXmin, j.Caps.wXmax), (j.Caps.wYmin, j.Caps.wYmax), (j.Caps.wZmin, j.Caps.wZmax),
                (j.Caps.wRmin, j.Caps.wRmax), (j.Caps.wUmin, j.Caps.wUmax), (j.Caps.wVmin, j.Caps.wVmax),
            };
            lock (_lock)
            {
                for (int a = 0; a < 6; a++)
                {
                    var (min, max) = range[a];
                    j.Axes[a] = max > min ? Math.Clamp((raw[a] - (double)min) / (max - min) * 2.0 - 1.0, -1, 1) : 0;
                }
                j.Buttons = info.dwButtons;
                j.Pov = info.dwPOV == 0xFFFF ? -1 : (int)info.dwPOV;
            }
        }
    }

    private void Rescan()
    {
        var found = new List<Joy>();
        var seen = new Dictionary<string, int>();
        uint n = Math.Min(joyGetNumDevs(), 16u);
        for (uint id = 0; id < n; id++)
        {
            var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
            if (joyGetPosEx(id, ref info) != 0) continue; // not plugged in
            var caps = new JOYCAPSW();
            if (joyGetDevCapsW((UIntPtr)id, ref caps, (uint)Marshal.SizeOf<JOYCAPSW>()) != 0) continue;
            if (caps.wMid == 0x045E && XboxPids.Contains(caps.wPid)) continue;

            string key = $"{caps.wMid:X4}:{caps.wPid:X4}";
            seen[key] = seen.TryGetValue(key, out int c) ? c + 1 : 0;
            found.Add(new Joy
            {
                Id = id,
                DeviceId = $"winmm:{key}:{seen[key]}",
                Name = OemName(caps) ?? caps.szPname,
                Caps = caps,
                AxisCount = (int)Math.Min(caps.wNumAxes, 6),
            });
        }
        lock (_lock)
        {
            // Keep live state objects for devices that are still present.
            var old = _joys.ToDictionary(j => j.DeviceId);
            _joys = found.Select(f => old.TryGetValue(f.DeviceId, out var o) && o.Id == f.Id ? o : f).ToList();
            _devices = _joys
                .Select(j => new DeviceInfo(j.DeviceId, j.Name, Name, (int)Math.Min(j.Caps.wNumButtons, MaxButtons), j.AxisCount))
                .ToList();
        }
    }

    private static string? OemName(JOYCAPSW caps)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{caps.wMid:X4}&PID_{caps.wPid:X4}");
            return k?.GetValue("OEMName") as string;
        }
        catch { return null; }
    }

    public bool TryRead(InputRef input, out double value)
    {
        value = 0;
        if (!input.DeviceId.StartsWith("winmm:", StringComparison.Ordinal)) return false;
        lock (_lock)
        {
            var j = _joys.FirstOrDefault(x => x.DeviceId == input.DeviceId);
            if (j is not null) value = Read(j, input.Kind, input.Index);
        }
        return true;
    }

    public IEnumerable<(InputRef Input, double Value)> ReadAll()
    {
        var result = new List<(InputRef, double)>();
        lock (_lock)
        {
            foreach (var j in _joys)
            {
                int buttons = (int)Math.Min(j.Caps.wNumButtons, MaxButtons);
                for (int b = 0; b < buttons; b++)
                    result.Add((new InputRef(j.DeviceId, InputKind.Button, b, $"{j.Name} Button {b + 1}"), Read(j, InputKind.Button, b)));
                if ((j.Caps.wCaps & JOYCAPS_HASPOV) != 0)
                    for (int p = 0; p < 4; p++)
                        result.Add((new InputRef(j.DeviceId, InputKind.Button, PovBase + p, $"{j.Name} {PovNames[p]}"), Read(j, InputKind.Button, PovBase + p)));
                for (int a = 0; a < j.AxisCount; a++)
                    result.Add((new InputRef(j.DeviceId, InputKind.Axis, a, $"{j.Name} Axis {AxisNames[a]}"), Read(j, InputKind.Axis, a)));
            }
        }
        return result;
    }

    private static double Read(Joy j, InputKind kind, int index)
    {
        switch (kind)
        {
            case InputKind.Button when index < MaxButtons:
                return (j.Buttons & (1u << index)) != 0 ? 1 : 0;
            case InputKind.Button when index is >= PovBase and < PovBase + 4:
                if (j.Pov < 0) return 0;
                double deg = j.Pov / 100.0;
                double target = (index - PovBase) * 90.0;
                double diff = Math.Abs(((deg - target) % 360 + 540) % 360 - 180);
                return diff < 67.5 ? 1 : 0; // diagonals press both neighbours
            case InputKind.Axis when index is >= 0 and < 6:
                // WinMM Y grows downwards; flip so "up" is positive like XInput.
                return index == 1 ? -j.Axes[index] : j.Axes[index];
            case InputKind.Trigger when index is >= 0 and < 6:
                return (j.Axes[index] + 1) / 2;
            default:
                return 0;
        }
    }

    private const uint JOY_RETURNALL = 0x000000FF;
    private const uint JOYCAPS_HASPOV = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos,
                    dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOYCAPSW
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax,
                    wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }

    [DllImport("winmm.dll")] private static extern uint joyGetNumDevs();
    [DllImport("winmm.dll")] private static extern uint joyGetPosEx(uint uJoyID, ref JOYINFOEX pji);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint joyGetDevCapsW(UIntPtr uJoyID, ref JOYCAPSW pjc, uint cbjc);
}
