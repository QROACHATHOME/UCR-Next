using System.Runtime.InteropServices;

namespace UcrNext.Devices;

public static class KeyMapping
{
    /// <summary>Scan code (with 0xE000 for extended keys) to Win32 virtual-key code; 0 if unknown.</summary>
    public static int ScanToVk(int scanCode) => (int)MapVirtualKey((uint)scanCode, MAPVK_VSC_TO_VK_EX);

    private const uint MAPVK_VSC_TO_VK_EX = 3;
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}
