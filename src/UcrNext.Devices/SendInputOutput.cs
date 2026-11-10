using System.Runtime.InteropServices;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>Keyboard and mouse output through Win32 SendInput. Keys are sent as scan codes for game compatibility.</summary>
public sealed class SendInputOutput : IKeyboardMouseOutput
{
    public void Key(int vk, bool down)
    {
        uint sc = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC_EX);
        uint flags = down ? 0u : KEYEVENTF_KEYUP;
        ushort scan = (ushort)(sc & 0xFF);
        if (scan != 0)
        {
            flags |= KEYEVENTF_SCANCODE;
            if ((sc & 0xFF00) is 0xE000 or 0xE100) flags |= KEYEVENTF_EXTENDEDKEY;
        }
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki = new KEYBDINPUT { wVk = scan != 0 ? (ushort)0 : (ushort)vk, wScan = scan, dwFlags = flags };
        Send(input);
    }

    public void MouseButton(MouseButton button, bool down)
    {
        (uint downFlag, uint upFlag, uint data) = button switch
        {
            Core.MouseButton.Left => (0x0002u, 0x0004u, 0u),
            Core.MouseButton.Right => (0x0008u, 0x0010u, 0u),
            Core.MouseButton.Middle => (0x0020u, 0x0040u, 0u),
            Core.MouseButton.X1 => (0x0080u, 0x0100u, 1u),
            _ => (0x0080u, 0x0100u, 2u),
        };
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi = new MOUSEINPUT { dwFlags = down ? downFlag : upFlag, mouseData = data };
        Send(input);
    }

    public void MouseMove(int dx, int dy)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE };
        Send(input);
    }

    public void MouseWheel(int delta, bool horizontal)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL };
        Send(input);
    }

    private static void Send(INPUT input)
    {
        var arr = new[] { input };
        SendInput(1, arr, Marshal.SizeOf<INPUT>());
    }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
    private const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MAPVK_VK_TO_VSC_EX = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
