using System.Runtime.InteropServices;
using System.Text;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>
/// Per-device keyboard and mouse-button input through the Interception driver
/// (https://github.com/oblitum/Interception), with optional blocking.
///
/// Safety rules, because every keystroke on the PC flows through this loop while it runs:
/// a stroke is always passed on if anything goes wrong; a key whose "down" was passed on
/// always gets its "up" passed on too (so nothing gets stuck); and if the process dies the
/// driver drops our filter and input flows normally again.
/// Mouse movement is not filtered at all, so it never gains latency.
/// </summary>
public sealed class InterceptionProvider : IInputProvider, IDisposable
{
    public const string KeyboardPrefix = "ikbd:";
    public const string MousePrefix = "imouse:";
    private const int MaxKey = 0x400; // scan code | E0 (0x100) | E1 (0x200)
    private static readonly string[] MouseNames = { "Left", "Right", "Middle", "X1", "X2" };

    private sealed class Dev
    {
        public required int Number;          // 1-10 keyboards, 11-20 mice
        public required string DeviceId;
        public required string Name;
        public readonly bool[] Pressed = new bool[MaxKey];
        public readonly bool[] PassedDown = new bool[MaxKey];
        public readonly bool[] BlockedDown = new bool[MaxKey];
        public bool IsMouse => Number > 10;
    }

    private readonly Func<InputRef, bool> _isBlocked;
    private readonly Func<bool> _anyBlocks;
    private readonly object _lock = new();
    private readonly Dev?[] _byNumber = new Dev?[21];
    private IntPtr _ctx;
    private Thread? _thread;
    private volatile bool _running;
    private Predicate? _isKeyboardPred, _isMousePred; // keep delegates alive
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();

    public InterceptionProvider(Func<InputRef, bool> isBlocked, Func<bool> anyBlocks)
    {
        _isBlocked = isBlocked;
        _anyBlocks = anyBlocks;
        Status = "Interception off";
    }

    public string Name => "Interception";
    public string Status { get; private set; }
    public bool IsRunning => _running;
    public IReadOnlyList<DeviceInfo> Devices => _devices;

    /// <summary>Loads interception.dll, opens the driver and starts the capture thread.</summary>
    public bool Start()
    {
        if (_running) return true;
        NativeLibraries.EnsureRegistered();
        try
        {
            _ctx = interception_create_context();
        }
        catch (DllNotFoundException)
        {
            Status = "interception.dll not found (copy it next to UCR-Next.exe)";
            return false;
        }
        catch (Exception ex)
        {
            Status = "Interception failed: " + ex.Message;
            return false;
        }
        if (_ctx == IntPtr.Zero)
        {
            Status = "Interception driver not installed (or reboot pending)";
            return false;
        }

        Rescan();
        _isKeyboardPred = d => d is >= 1 and <= 10 ? 1 : 0;
        _isMousePred = d => d is >= 11 and <= 20 ? 1 : 0;
        interception_set_filter(_ctx, _isKeyboardPred, FILTER_KEY_ALL);
        interception_set_filter(_ctx, _isMousePred, FILTER_MOUSE_BUTTONS);

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "UcrNext Interception", Priority = ThreadPriority.Highest };
        _thread.Start();
        Status = $"Interception: {_devices.Count} device(s)";
        return true;
    }

    private void Loop()
    {
        var stroke = new Stroke();
        long nextScan = Environment.TickCount64 + 3000;
        while (_running)
        {
            int device = interception_wait_with_timeout(_ctx, 100);
            if (device <= 0)
            {
                if (Environment.TickCount64 >= nextScan) { Rescan(); nextScan = Environment.TickCount64 + 3000; }
                continue;
            }
            if (interception_receive(_ctx, device, ref stroke, 1) <= 0) continue;

            bool pass = true;
            try
            {
                pass = device > 10 ? HandleMouse(device, ref stroke) : HandleKey(device, ref stroke);
            }
            catch
            {
                pass = true; // never swallow input because of our own bug
            }
            if (pass) interception_send(_ctx, device, ref stroke, 1);
        }
    }

    private Dev GetDev(int number)
    {
        var d = _byNumber[number];
        if (d is not null) return d;
        Rescan();
        return _byNumber[number] ?? new Dev { Number = number, DeviceId = (number > 10 ? MousePrefix : KeyboardPrefix) + "unknown#" + number, Name = "Device " + number };
    }

    private bool HandleKey(int device, ref Stroke s)
    {
        var d = GetDev(device);
        int idx = s.Code | ((s.State & KEY_E0) != 0 ? 0x100 : 0) | ((s.State & KEY_E1) != 0 ? 0x200 : 0);
        if (idx >= MaxKey) return true;
        bool down = (s.State & KEY_UP) == 0;
        lock (_lock) d.Pressed[idx] = down;
        return Decide(d, idx, down);
    }

    private bool HandleMouse(int device, ref Stroke s)
    {
        var d = GetDev(device);
        ushort state = s.MouseState;
        ushort keep = state;
        for (int b = 0; b < 5; b++)
        {
            ushort downBit = (ushort)(1 << (b * 2)), upBit = (ushort)(1 << (b * 2 + 1));
            if ((state & downBit) != 0)
            {
                lock (_lock) d.Pressed[b] = true;
                if (!Decide(d, b, true)) keep &= (ushort)~downBit;
            }
            if ((state & upBit) != 0)
            {
                lock (_lock) d.Pressed[b] = false;
                if (!Decide(d, b, false)) keep &= (ushort)~upBit;
            }
        }
        if (keep == state) return true;
        s.MouseState = keep;
        // Drop the stroke only if nothing else (movement, wheel) is left in it.
        return keep != 0 || s.X != 0 || s.Y != 0;
    }

    /// <summary>Block/pass decision that can never leave a key stuck down in Windows.</summary>
    private bool Decide(Dev d, int idx, bool down)
    {
        if (down)
        {
            if (d.PassedDown[idx]) return true;   // auto-repeat of a key we let through
            if (d.BlockedDown[idx]) return false; // auto-repeat of a key we block
            if (_anyBlocks() && _isBlocked(new InputRef(d.DeviceId, InputKind.Button, idx)))
            {
                d.BlockedDown[idx] = true;
                return false;
            }
            d.PassedDown[idx] = true;
            return true;
        }
        if (d.PassedDown[idx]) { d.PassedDown[idx] = false; return true; }
        if (d.BlockedDown[idx]) { d.BlockedDown[idx] = false; return false; }
        return true;
    }

    private void Rescan()
    {
        var list = new List<Dev>();
        var counts = new Dictionary<string, int>();
        var buf = new byte[1024];
        for (int n = 1; n <= 20; n++)
        {
            uint len = interception_get_hardware_id(_ctx, n, buf, (uint)buf.Length);
            if (len == 0 || len > buf.Length) { _byNumber[n] = null; continue; }
            string hwid = Encoding.Unicode.GetString(buf, 0, (int)len).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown";
            bool mouse = n > 10;
            string key = (mouse ? "m" : "k") + hwid;
            counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
            string id = (mouse ? MousePrefix : KeyboardPrefix) + hwid + "#" + counts[key];
            var existing = _byNumber[n];
            if (existing is not null && existing.DeviceId == id) { list.Add(existing); continue; }
            var d = new Dev { Number = n, DeviceId = id, Name = FriendlyName(mouse, hwid, counts[key]) };
            _byNumber[n] = d;
            list.Add(d);
        }
        _devices = list.Select(d => new DeviceInfo(d.DeviceId, d.Name, Name, d.IsMouse ? 5 : 256, 0)).ToList();
    }

    private static string FriendlyName(bool mouse, string hwid, int ordinal)
    {
        string kind = mouse ? "Mouse" : "Keyboard";
        var vid = System.Text.RegularExpressions.Regex.Match(hwid, @"VID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var pid = System.Text.RegularExpressions.Regex.Match(hwid, @"PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string ids = vid.Success && pid.Success ? $"{vid.Groups[1].Value}:{pid.Groups[1].Value}" : hwid;
        return $"{kind} {ids}" + (ordinal > 1 ? $" #{ordinal}" : "");
    }

    public void Poll() { }

    public bool TryRead(InputRef input, out double value)
    {
        value = 0;
        bool kb = input.DeviceId.StartsWith(KeyboardPrefix, StringComparison.Ordinal);
        if (!kb && !input.DeviceId.StartsWith(MousePrefix, StringComparison.Ordinal)) return false;
        lock (_lock)
        {
            foreach (var d in _byNumber)
                if (d is not null && d.DeviceId == input.DeviceId && input.Index is >= 0 and < MaxKey)
                {
                    value = d.Pressed[input.Index] ? 1 : 0;
                    break;
                }
        }
        return true;
    }

    public IEnumerable<(InputRef Input, double Value)> ReadAll()
    {
        var list = new List<(InputRef, double)>();
        lock (_lock)
        {
            foreach (var d in _byNumber)
            {
                if (d is null) continue;
                for (int i = 0; i < (d.IsMouse ? 5 : MaxKey); i++)
                {
                    if (!d.Pressed[i]) continue;
                    string label = d.IsMouse ? $"{d.Name} {MouseNames[i]}" : $"{d.Name} Key {KeyName(i)}";
                    list.Add((new InputRef(d.DeviceId, InputKind.Button, i, label), 1));
                }
            }
        }
        return list;
    }

    /// <summary>Display name for a key index (scan code | E0 flag).</summary>
    public static string KeyName(int idx)
    {
        uint sc = (uint)(idx & 0xFF) | ((idx & 0x100) != 0 ? 0xE000u : 0u);
        uint vk = MapVirtualKey(sc, MAPVK_VSC_TO_VK_EX);
        return vk != 0 ? VirtualKeys.Name((int)vk) : $"SC 0x{idx:X3}";
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
        if (_ctx != IntPtr.Zero)
        {
            try { interception_destroy_context(_ctx); } catch { }
            _ctx = IntPtr.Zero;
        }
        Status = "Interception off";
    }

    private const ushort KEY_UP = 0x01, KEY_E0 = 0x02, KEY_E1 = 0x04;
    private const ushort FILTER_KEY_ALL = 0xFFFF;
    private const ushort FILTER_MOUSE_BUTTONS = 0x03FF; // all button down/up flags, no move/wheel
    private const uint MAPVK_VSC_TO_VK_EX = 3;

    /// <summary>InterceptionStroke: big enough for both key and mouse strokes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct Stroke
    {
        // Key stroke
        [FieldOffset(0)] public ushort Code;
        [FieldOffset(2)] public ushort State;
        [FieldOffset(4)] public uint KeyInformation;
        // Mouse stroke
        [FieldOffset(0)] public ushort MouseState;
        [FieldOffset(2)] public ushort Flags;
        [FieldOffset(4)] public short Rolling;
        [FieldOffset(8)] public int X;
        [FieldOffset(12)] public int Y;
        [FieldOffset(16)] public uint MouseInformation;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Predicate(int device);

    private const string Dll = NativeLibraries.Interception;
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr interception_create_context();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void interception_destroy_context(IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void interception_set_filter(IntPtr ctx, Predicate predicate, ushort filter);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int interception_wait_with_timeout(IntPtr ctx, uint ms);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int interception_receive(IntPtr ctx, int device, ref Stroke stroke, uint n);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int interception_send(IntPtr ctx, int device, ref Stroke stroke, uint n);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern uint interception_get_hardware_id(IntPtr ctx, int device, byte[] buffer, uint size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}
