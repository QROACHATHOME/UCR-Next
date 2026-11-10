using System.Runtime.InteropServices;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>
/// vJoy virtual joystick output through vJoyInterface.dll, which ships with the vJoy driver
/// (looked up next to UCR-Next.exe, then in %ProgramFiles%\vJoy\x64). Not bundled.
/// </summary>
public sealed class VJoyOutput : IVJoyOutput, IDisposable
{
    private const string Dll = "vJoyInterface.dll";
    private static readonly uint[] AxisUsage = { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37 }; // HID X..SL1

    private sealed class Dev
    {
        public required VJoyDeviceInfo Info;
        public readonly int[] AxisMax = new int[8];
        public bool Acquired;
        public VJoyState? Last;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, Dev> _devs = new();

    public VJoyOutput()
    {
        NativeLibraries.EnsureRegistered();
        try
        {
            if (!vJoyEnabled())
            {
                Status = "vJoy driver installed but not enabled";
                return;
            }
            for (uint id = 1; id <= 16; id++)
            {
                int st = GetVJDStatus(id);
                if (st is VJD_STAT_MISS or VJD_STAT_UNKN) continue;
                var axes = new List<VJoyAxis>();
                var dev = new Dev { Info = null! };
                for (int a = 0; a < AxisUsage.Length; a++)
                {
                    if (!GetVJDAxisExist(id, AxisUsage[a])) continue;
                    axes.Add((VJoyAxis)a);
                    int max = 0;
                    dev.AxisMax[a] = GetVJDAxisMax(id, AxisUsage[a], ref max) && max > 0 ? max : 0x8000;
                }
                int buttons = Math.Clamp(GetVJDButtonNumber(id), 0, VJoyState.MaxButtons);
                int cont = Math.Clamp(GetVJDContPovNumber(id), 0, OutputRef.MaxPovHats);
                int disc = Math.Clamp(GetVJDDiscPovNumber(id), 0, OutputRef.MaxPovHats);
                dev.Info = new VJoyDeviceInfo((int)id, axes, buttons, cont > 0 ? cont : disc, cont > 0 || disc == 0);
                _devs[(int)id] = dev;
            }
            Devices = _devs.Values.Select(d => d.Info).ToList();
            Status = Devices.Count == 0 ? "vJoy: no devices configured (use 'Configure vJoy')" : $"vJoy: {Devices.Count} device(s)";
        }
        catch (DllNotFoundException)
        {
            Status = "vJoy not installed";
        }
        catch (EntryPointNotFoundException)
        {
            Status = "vJoyInterface.dll is too old";
        }
    }

    public bool IsAvailable => Devices.Count > 0;
    public string Status { get; private set; } = "vJoy not installed";
    public IReadOnlyList<VJoyDeviceInfo> Devices { get; private set; } = Array.Empty<VJoyDeviceInfo>();

    public void Update(int device, VJoyState state)
    {
        lock (_lock)
        {
            if (!_devs.TryGetValue(device, out var d)) return;
            if (!d.Acquired)
            {
                int st = GetVJDStatus((uint)device);
                if (st == VJD_STAT_BUSY) { Status = $"vJoy {device} is in use by another program"; return; }
                if (st != VJD_STAT_OWN && !AcquireVJD((uint)device)) { Status = $"vJoy {device}: acquire failed"; return; }
                ResetVJD((uint)device);
                d.Acquired = true;
                d.Last = null;
                Status = $"vJoy {device} acquired";
            }

            var last = d.Last;
            foreach (var a in d.Info.Axes)
            {
                int i = (int)a;
                if (last is not null && last.Axes[i] == state.Axes[i]) continue;
                int max = d.AxisMax[i];
                int value = (int)Math.Round((Math.Clamp(state.Axes[i], -1, 1) + 1) / 2 * max);
                SetAxis(value, (uint)device, AxisUsage[i]);
            }
            for (int b = 0; b < d.Info.Buttons; b++)
            {
                if (last is not null && last.Buttons[b] == state.Buttons[b]) continue;
                SetBtn(state.Buttons[b], (uint)device, (byte)(b + 1));
            }
            for (int h = 1; h <= d.Info.Povs; h++)
            {
                int angle = state.PovAngle(h);
                if (last is not null && last.PovAngle(h) == angle) continue;
                SetPov(d, device, h, angle);
            }
            d.Last = state.Clone();
        }
    }

    public void Release(int device)
    {
        lock (_lock)
        {
            if (!_devs.TryGetValue(device, out var d) || !d.Acquired) return;
            try
            {
                // Centre axes and release buttons before letting go.
                foreach (var a in d.Info.Axes) SetAxis(d.AxisMax[(int)a] / 2, (uint)device, AxisUsage[(int)a]);
                for (int b = 0; b < d.Info.Buttons; b++) SetBtn(false, (uint)device, (byte)(b + 1));
                for (int h = 1; h <= d.Info.Povs; h++) SetPov(d, device, h, -1);
                RelinquishVJD((uint)device);
            }
            catch { /* driver gone */ }
            d.Acquired = false;
            d.Last = null;
        }
    }

    /// <summary>
    /// Continuous hats take hundredths of a degree (-1 = centred). 4-direction hats take 0-3
    /// (up, right, down, left); a diagonal shows its up/down part.
    /// </summary>
    private static void SetPov(Dev d, int device, int hat, int angle)
    {
        if (d.Info.ContinuousPovs) SetContPov(angle < 0 ? -1 : angle * 100, (uint)device, (byte)hat);
        else
        {
            int dir = angle switch { < 0 => -1, 0 or 45 or 315 => 0, 90 => 1, 135 or 180 or 225 => 2, _ => 3 };
            SetDiscPov(dir, (uint)device, (byte)hat);
        }
    }

    public void Dispose()
    {
        foreach (var id in _devs.Keys.ToList()) Release(id);
    }

    private const int VJD_STAT_OWN = 0, VJD_STAT_FREE = 1, VJD_STAT_BUSY = 2, VJD_STAT_MISS = 3, VJD_STAT_UNKN = 4;

    [DllImport(Dll)] private static extern bool vJoyEnabled();
    [DllImport(Dll)] private static extern int GetVJDStatus(uint rID);
    [DllImport(Dll)] private static extern bool AcquireVJD(uint rID);
    [DllImport(Dll)] private static extern void RelinquishVJD(uint rID);
    [DllImport(Dll)] private static extern bool ResetVJD(uint rID);
    [DllImport(Dll)] private static extern bool GetVJDAxisExist(uint rID, uint axis);
    [DllImport(Dll)] private static extern bool GetVJDAxisMax(uint rID, uint axis, ref int max);
    [DllImport(Dll)] private static extern int GetVJDButtonNumber(uint rID);
    [DllImport(Dll)] private static extern bool SetAxis(int value, uint rID, uint axis);
    [DllImport(Dll)] private static extern bool SetBtn(bool value, uint rID, byte nBtn);
    [DllImport(Dll)] private static extern int GetVJDContPovNumber(uint rID);
    [DllImport(Dll)] private static extern int GetVJDDiscPovNumber(uint rID);
    [DllImport(Dll)] private static extern bool SetContPov(int value, uint rID, byte nPov);
    [DllImport(Dll)] private static extern bool SetDiscPov(int value, uint rID, byte nPov);
}
