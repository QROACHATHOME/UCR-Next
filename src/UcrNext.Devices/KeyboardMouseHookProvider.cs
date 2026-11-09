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
    private IntPtr _kbHook, _mouseHook;
    private HookProc? _kbProc, _mouseProc; // keep delegates alive

    public string Name => "Keyboard / Mouse";

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
                if (down || up) Volatile.Write(ref _keys[info.vkCode], down);
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
                switch (wParam.ToInt32())
                {
                    case 0x0201: _mouse[0] = true; break;  // WM_LBUTTONDOWN
                    case 0x0202: _mouse[0] = false; break; // WM_LBUTTONUP
                    case 0x0204: _mouse[1] = true; break;  // WM_RBUTTONDOWN
                    case 0x0205: _mouse[1] = false; break;
                    case 0x0207: _mouse[2] = true; break;  // WM_MBUTTONDOWN
                    case 0x0208: _mouse[2] = false; break;
                    case 0x020B: _mouse[XIndex(info.mouseData)] = true; break;  // WM_XBUTTONDOWN
                    case 0x020C: _mouse[XIndex(info.mouseData)] = false; break; // WM_XBUTTONUP
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
