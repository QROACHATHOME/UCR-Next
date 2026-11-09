using System.Diagnostics;
using System.Runtime.InteropServices;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>
/// Physical keyboard and mouse buttons via low-level hooks. Observes only (does not block).
/// Injected input (including our own SendInput output) is ignored to avoid feedback loops.
/// <see cref="Install"/> must be called on a thread that runs a message loop (the UI thread).
/// </summary>
public sealed class KeyboardMouseHookProvider : IInputProvider, IDisposable
{
    public const string KeyboardId = "keyboard";
    public const string MouseId = "mouse";
    private static readonly string[] MouseNames = { "Left", "Right", "Middle", "X1", "X2" };

    private readonly bool[] _keys = new bool[256];
    private readonly bool[] _mouse = new bool[5];
    // Block bookkeeping: a key whose "down" was passed on always gets its "up" passed on.
    private readonly bool[] _passed = new bool[256 + 5];
    private readonly bool[] _blockedDown = new bool[256 + 5];
    private readonly Func<InputRef, bool> _isBlocked;
    private readonly Func<bool> _anyBlocks;
    private IntPtr _kbHook, _mouseHook;
    private HookProc? _kbProc, _mouseProc; // keep delegates alive

    public KeyboardMouseHookProvider(Func<InputRef, bool>? isBlocked = null, Func<bool>? anyBlocks = null)
    {
        _isBlocked = isBlocked ?? (_ => false);
        _anyBlocks = anyBlocks ?? (() => false);
    }

    /// <summary>Returns false if the event must be swallowed. <paramref name="slot"/>: 0-255 keys, 256+ mouse.</summary>
    private bool Decide(int slot, InputRef input, bool down)
    {
        if (down)
        {
            if (_passed[slot]) return true;
            if (_blockedDown[slot]) return false;
            if (_anyBlocks() && _isBlocked(input)) { _blockedDown[slot] = true; return false; }
            _passed[slot] = true;
            return true;
        }
        if (_passed[slot]) { _passed[slot] = false; return true; }
        if (_blockedDown[slot]) { _blockedDown[slot] = false; return false; }
        return true;
    }

    public string Name => "Keyboard / Mouse";

    /// <summary>Raised (on the UI thread) when Ctrl+Alt+Pause is pressed.</summary>
    public event Action? PanicPressed;

    public IReadOnlyList<DeviceInfo> Devices { get; } = new[]
    {
        new DeviceInfo(KeyboardId, "Keyboard (all keyboards)", "Hooks", 256, 0),
        new DeviceInfo(MouseId, "Mouse (all mice)", "Hooks", 5, 0),
    };

    public void Install()
    {
        if (_kbHook != IntPtr.Zero) return;
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
        IntPtr mod = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
    }

    public void Poll() { }

    public bool TryRead(InputRef input, out double value)
    {
        value = 0;
        if (input.DeviceId == KeyboardId)
        {
            if (input.Index is >= 0 and < 256) value = Volatile.Read(ref _keys[input.Index]) ? 1 : 0;
            return true;
        }
        if (input.DeviceId == MouseId)
        {
            if (input.Index is >= 0 and < 5) value = Volatile.Read(ref _mouse[input.Index]) ? 1 : 0;
            return true;
        }
        return false;
    }

    /// <summary>Only returns pressed controls (released ones read as 0 anyway).</summary>
    public IEnumerable<(InputRef Input, double Value)> ReadAll()
    {
        var list = new List<(InputRef, double)>();
        for (int k = 0; k < 256; k++)
            if (_keys[k]) list.Add((new InputRef(KeyboardId, InputKind.Button, k, "Key " + VirtualKeys.Name(k)), 1));
        for (int m = 0; m < 5; m++)
            if (_mouse[m]) list.Add((new InputRef(MouseId, InputKind.Button, m, "Mouse " + MouseNames[m]), 1));
        return list;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLKHF_INJECTED) == 0 && info.vkCode < 256)
            {
                int msg = wParam.ToInt32();
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = msg is WM_KEYUP or WM_SYSKEYUP;
                if (down || up)
                {
                    int vk = (int)info.vkCode;
                    Volatile.Write(ref _keys[vk], down);
                    // Panic hotkey Ctrl+Alt+Pause: always passes through, raises the event.
                    if (down && (vk == VK_PAUSE || vk == VK_CANCEL) && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                    {
                        try { PanicPressed?.Invoke(); } catch { }
                    }
                    bool pass = true;
                    try { pass = Decide(vk, new InputRef(KeyboardId, InputKind.Button, vk), down); } catch { }
                    if (!pass) return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) == 0)
            {
                int button = -1;
                bool down = false;
                switch (wParam.ToInt32())
                {
                    case 0x0201: button = 0; down = true; break; // WM_LBUTTONDOWN
                    case 0x0202: button = 0; break;              // WM_LBUTTONUP
                    case 0x0204: button = 1; down = true; break; // WM_RBUTTONDOWN
                    case 0x0205: button = 1; break;
                    case 0x0207: button = 2; down = true; break; // WM_MBUTTONDOWN
                    case 0x0208: button = 2; break;
                    case 0x020B: button = XIndex(info.mouseData); down = true; break; // WM_XBUTTONDOWN
                    case 0x020C: button = XIndex(info.mouseData); break;              // WM_XBUTTONUP
                }
                if (button >= 0)
                {
                    _mouse[button] = down;
                    bool pass = true;
                    try { pass = Decide(256 + button, new InputRef(MouseId, InputKind.Button, button), down); } catch { }
                    if (!pass) return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static int XIndex(uint mouseData) => (mouseData >> 16) == 2 ? 4 : 3;

    public void Dispose()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _kbHook = _mouseHook = IntPtr.Zero;
    }

    private const int VK_PAUSE = 0x13, VK_CANCEL = 0x03, VK_CONTROL = 0x11, VK_MENU = 0x12;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
