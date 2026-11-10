using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>
/// Per-device keyboards and mice through Windows Raw Input — no driver needed, but read-only
/// (Raw Input cannot stop the original key/click from reaching other programs; use Interception for that).
///
/// Mice ("rawmouse:VID:PID#n", or "mouse" = any mouse):
///  - Delta inputs: index 0 = X, 1 = Y (counts since the last engine tick).
///  - Buttons: 0-4 = Left, Right, Middle, X1, X2 (per device only — "any mouse" buttons come from the
///    hooks, which can also block); 5-8 = Wheel Up, Wheel Down, Wheel Right, Wheel Left (short pulses).
/// Keyboards ("rawkbd:VID:PID#n"):
///  - Buttons indexed by scan code | 0x100 for E0 keys | 0x200 for E1 — the same scheme as the
///    Interception provider, so per-keyboard bindings carry over between the two.
///
/// Injected input (hDevice = 0, e.g. our own SendInput) is ignored to avoid feedback loops.
/// <see cref="Install"/> must be called on a thread with a message loop (the UI thread).
/// </summary>
public sealed class RawInputProvider : IInputProvider, IDeltaSource, IDisposable
{
    public const string AnyMouse = KeyboardMouseHookProvider.MouseId;
    public const string MousePrefix = "rawmouse:";
    public const string KeyboardPrefix = "rawkbd:";
    public const int WheelPulseMs = 60;
    private const int MaxKey = 0x400;
    private static readonly string[] ButtonNames = { "Left", "Right", "Middle", "X1", "X2", "Wheel Up", "Wheel Down", "Wheel Right", "Wheel Left" };

    private sealed class Dev
    {
        public required string Id;
        public required string Name;
        public required bool IsKeyboard;
        public double PendX, PendY, CurX, CurY, BindX, BindY;
        public int PendN, CurN;
        public readonly bool[] Buttons = new bool[5];
        public readonly long[] WheelUntil = new long[4];
        public bool[]? Keys;
    }

    private readonly object _lock = new();
    private readonly Dictionary<IntPtr, Dev> _byHandle = new();
    private readonly Dev _any = new() { Id = AnyMouse, Name = "Mouse (all mice)", IsKeyboard = false };
    private readonly Dictionary<string, int> _ordinals = new();
    private IntPtr _hwnd;
    private WndProc? _wndProc; // keep alive
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();
    private long _nextScan;

    public string Name => "Raw Input";
    public IReadOnlyList<DeviceInfo> Devices => _devices;

    public void Install()
    {
        if (_hwnd != IntPtr.Zero) return;
        _wndProc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "UcrNextRawInput",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, "UcrNextRawInput", "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        var rid = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd }, // mice
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd }, // keyboards
        };
        RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Rescan();
    }

    private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { OnInput(lParam); } catch { /* never break the message loop */ }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private readonly byte[] _buf = new byte[64];

    private void OnInput(IntPtr hRaw)
    {
        uint size = (uint)_buf.Length;
        var h = GCHandle.Alloc(_buf, GCHandleType.Pinned);
        try
        {
            if (GetRawInputData(hRaw, RID_INPUT, h.AddrOfPinnedObject(), ref size, (uint)HeaderSize) == unchecked((uint)-1)) return;
        }
        finally { h.Free(); }
        uint type = BitConverter.ToUInt32(_buf, 0);
        IntPtr device = (IntPtr)BitConverter.ToInt64(_buf, 8);
        if (device == IntPtr.Zero) return; // injected (SendInput) — ignore to avoid feedback
        int o = HeaderSize;

        if (type == RIM_TYPEKEYBOARD)
        {
            ushort makeCode = BitConverter.ToUInt16(_buf, o);
            ushort flags = BitConverter.ToUInt16(_buf, o + 2);
            if (makeCode == 0 || makeCode == 0xFF) return; // overrun / fake codes
            int idx = (makeCode & 0xFF) | ((flags & RI_KEY_E0) != 0 ? 0x100 : 0) | ((flags & RI_KEY_E1) != 0 ? 0x200 : 0);
            bool down = (flags & RI_KEY_BREAK) == 0;
            lock (_lock)
            {
                var dev = DevFor(device, keyboard: true);
                if (idx < MaxKey) dev.Keys![idx] = down;
            }
            return;
        }
        if (type != RIM_TYPEMOUSE) return;

        ushort mflags = BitConverter.ToUInt16(_buf, o);
        ushort buttonFlags = BitConverter.ToUInt16(_buf, o + 4);
        short buttonData = BitConverter.ToInt16(_buf, o + 6);
        int dx = BitConverter.ToInt32(_buf, o + 12);
        int dy = BitConverter.ToInt32(_buf, o + 16);
        bool relative = (mflags & MOUSE_MOVE_ABSOLUTE) == 0;
        long now = Environment.TickCount64;

        lock (_lock)
        {
            var dev = DevFor(device, keyboard: false);
            foreach (var d in new[] { dev, _any })
            {
                if (relative && (dx != 0 || dy != 0))
                {
                    d.PendX += dx; d.PendY += dy; d.PendN++;
                    d.BindX += dx; d.BindY += dy;
                }
                if ((buttonFlags & RI_MOUSE_WHEEL) != 0 && buttonData != 0) d.WheelUntil[buttonData > 0 ? 0 : 1] = now + WheelPulseMs;
                if ((buttonFlags & RI_MOUSE_HWHEEL) != 0 && buttonData != 0) d.WheelUntil[buttonData > 0 ? 2 : 3] = now + WheelPulseMs;
            }
            for (int b = 0; b < 5; b++)
            {
                if ((buttonFlags & (1 << (b * 2))) != 0) dev.Buttons[b] = true;
                if ((buttonFlags & (1 << (b * 2 + 1))) != 0) dev.Buttons[b] = false;
            }
        }
    }

    private Dev DevFor(IntPtr handle, bool keyboard)
    {
        if (_byHandle.TryGetValue(handle, out var d)) return d;
        string path = DeviceName(handle) ?? "";
        var m = Regex.Match(path, @"VID_([0-9A-F]{4}).*?PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        string key = m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}" : "0000:0000";
        string prefix = keyboard ? KeyboardPrefix : MousePrefix;
        int n = _ordinals[prefix + key] = _ordinals.TryGetValue(prefix + key, out var c) ? c + 1 : 1;
        string kind = keyboard ? "Keyboard" : "Mouse";
        // The product name the device reports ("I-PAC 2 (Ultimarc)"); the id keeps VID:PID so bindings don't change.
        string? product = m.Success ? HidNames.Describe(path) : null;
        d = new Dev
        {
            Id = $"{prefix}{key}#{n}",
            Name = (product is not null ? $"{product} ({kind.ToLowerInvariant()})"
                    : m.Success ? $"{kind} {key}" : $"{kind} (built-in / unknown)") + (n > 1 ? $" #{n}" : ""),
            IsKeyboard = keyboard,
            Keys = keyboard ? new bool[MaxKey] : null,
        };
        _byHandle[handle] = d;
        _devices = _byHandle.Values.OrderBy(x => x.IsKeyboard).ThenBy(x => x.Id)
            .Select(x => new DeviceInfo(x.Id, x.Name, Name, x.IsKeyboard ? 256 : 9, x.IsKeyboard ? 0 : 2)).ToList();
        return d;
    }

    private void Rescan()
    {
        uint count = 0, cb = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, cb) != 0 || count == 0) return;
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, cb) == unchecked((uint)-1)) return;
        lock (_lock)
            foreach (var d in list.Take((int)count).Where(d => d.dwType is RIM_TYPEMOUSE or RIM_TYPEKEYBOARD))
                DevFor(d.hDevice, d.dwType == RIM_TYPEKEYBOARD);
    }

    /// <summary>Called by the engine thread each tick: publishes the movement gathered since the last tick.</summary>
    public void Poll()
    {
        lock (_lock)
        {
            foreach (var d in _byHandle.Values.Append(_any))
            {
                if (d.IsKeyboard) continue;
                d.CurX = d.PendX; d.CurY = d.PendY; d.CurN = d.PendN;
                d.PendX = d.PendY = 0; d.PendN = 0;
            }
        }
        long now = Environment.TickCount64;
        if (now >= _nextScan) { _nextScan = now + 5000; try { Rescan(); } catch { } }
    }

    private Dev? Find(string id) => id == AnyMouse ? _any : _byHandle.Values.FirstOrDefault(d => d.Id == id);

    public bool TryReadDelta(InputRef input, out double sum, out int events)
    {
        sum = 0; events = 0;
        if (input.Kind != InputKind.Delta) return false;
        bool ours = input.DeviceId == AnyMouse || input.DeviceId.StartsWith(MousePrefix, StringComparison.Ordinal);
        if (!ours) return false;
        lock (_lock)
        {
            var d = Find(input.DeviceId);
            if (d is not null) { sum = input.Index == 1 ? d.CurY : d.CurX; events = d.CurN; }
        }
        return true;
    }

    public bool TryRead(InputRef input, out double value)
    {
        value = 0;
        string id = input.DeviceId;
        if (id.StartsWith(KeyboardPrefix, StringComparison.Ordinal))
        {
            lock (_lock)
                if (Find(id) is { Keys: { } keys } && input.Index is >= 0 and < MaxKey) value = keys[input.Index] ? 1 : 0;
            return true;
        }
        bool any = id == AnyMouse;
        if (!any && !id.StartsWith(MousePrefix, StringComparison.Ordinal)) return false;
        // "Any mouse" buttons 0-4 belong to the hook provider (which can block them).
        if (any && input.Kind == InputKind.Button && input.Index < 5) return false;
        lock (_lock)
        {
            var d = Find(id);
            if (d is null) return true;
            if (input.Kind == InputKind.Delta) value = input.Index == 1 ? d.CurY : d.CurX;
            else if (input.Kind == InputKind.Button && input.Index is >= 0 and < 5) value = d.Buttons[input.Index] ? 1 : 0;
            else if (input.Kind == InputKind.Button && input.Index is >= 5 and < 9) value = Environment.TickCount64 < d.WheelUntil[input.Index - 5] ? 1 : 0;
        }
        return true;
    }

    /// <summary>
    /// For press-to-bind: movement since the previous call (≈20 counts = full deflection),
    /// pressed per-device mouse buttons, wheel pulses and pressed per-keyboard keys.
    /// </summary>
    public IEnumerable<(InputRef Input, double Value)> ReadAll()
    {
        var list = new List<(InputRef, double)>();
        var anyKeys = new HashSet<int>();
        long now = Environment.TickCount64;
        lock (_lock)
        {
            foreach (var d in _byHandle.Values.Append(_any))
            {
                if (d.IsKeyboard)
                {
                    for (int k = 0; k < MaxKey; k++)
                    {
                        if (!d.Keys![k]) continue;
                        list.Add((new InputRef(d.Id, InputKind.Button, k, $"{d.Name} Key {InterceptionProvider.KeyName(k)}"), 1));
                        // Also report it as an "any keyboard" key (same virtual-key code the hook uses).
                        // Windows stops sending keys to the low-level keyboard hook while one of our own
                        // windows has focus once Raw Input keyboards are registered — exactly when the user
                        // presses a key in the bind dialog — so the hook alone can't see these.
                        int vk = KeyMapping.ScanToVk((k & 0xFF) | ((k & 0x100) != 0 ? 0xE000 : 0));
                        if (vk != 0 && anyKeys.Add(vk))
                            list.Add((new InputRef(KeyboardMouseHookProvider.KeyboardId, InputKind.Button, vk, "Key " + VirtualKeys.Name(vk)), 1));
                    }
                    continue;
                }
                list.Add((new InputRef(d.Id, InputKind.Delta, 0, $"{d.Name} movement X"), Math.Clamp(d.BindX / 20.0, -1, 1)));
                list.Add((new InputRef(d.Id, InputKind.Delta, 1, $"{d.Name} movement Y"), Math.Clamp(d.BindY / 20.0, -1, 1)));
                d.BindX = d.BindY = 0;
                if (d != _any)
                    for (int b = 0; b < 5; b++)
                        if (d.Buttons[b]) list.Add((new InputRef(d.Id, InputKind.Button, b, $"{d.Name} {ButtonNames[b]}"), 1));
                for (int w = 0; w < 4; w++)
                    if (now < d.WheelUntil[w]) list.Add((new InputRef(d.Id, InputKind.Button, 5 + w, $"{d.Name} {ButtonNames[5 + w]}"), 1));
            }
        }
        return list;
    }

    private static string? DeviceName(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return null;
        var buf = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buf, ref chars) == unchecked((uint)-1)) return null;
            return Marshal.PtrToStringUni(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        var rid = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
        };
        RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    // ---- interop ----
    private static readonly int HeaderSize = Marshal.SizeOf<RAWINPUTHEADER>();
    private const uint WM_INPUT = 0x00FF, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1;
    private const ushort MOUSE_MOVE_ABSOLUTE = 0x01, RI_MOUSE_WHEEL = 0x0400, RI_MOUSE_HWHEEL = 0x0800;
    private const ushort RI_KEY_BREAK = 0x01, RI_KEY_E0 = 0x02, RI_KEY_E1 = 0x04;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y,
        int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
