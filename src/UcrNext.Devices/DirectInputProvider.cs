using Vortice.DirectInput;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>
/// DirectInput game controllers (sticks, wheels, HOTAS, generic pads): up to 8 axes
/// (X, Y, Z, Rx, Ry, Rz, 2 sliders), 128 buttons and 4 POV hats per device.
/// XInput (Xbox) controllers are skipped — <see cref="XInputProvider"/> reads those
/// (this also hides our own ViGEm pad). Devices are identified by their DirectInput
/// instance GUID, which stays the same across reboots and replugging on the same port.
/// All DirectInput calls happen on the engine thread (inside <see cref="Poll"/>).
/// </summary>
public sealed class DirectInputProvider : IInputProvider, IDisposable
{
    public const int AxisCount = 8;
    public const int MaxButtons = 128;
    public const int PovBase = 128; // buttons 128..143 = POV n * 4 + (up, right, down, left)
    private static readonly string[] AxisNames = { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider 1", "Slider 2" };
    private static readonly string[] PovDirs = { "Up", "Right", "Down", "Left" };
    private const int RangeMax = 65535;

    private sealed class Pad
    {
        public required IDirectInputDevice8 Device;
        public required Guid Instance;
        public required int Vid, Pid;
        public string DeviceId = "";
        /// <summary>Older "dinput:VID:PID#n" id (n in key order among identical devices).</summary>
        public string AltId = "";
        /// <summary>USB serial number or port hash (see DeviceKeys).</summary>
        public string Key = "";
        public required string BaseName;
        public string Name = "";
        public int Buttons, Povs;
        public readonly double[] Axes = new double[AxisCount];
        public readonly bool[] AxisPresent = new bool[AxisCount];
        public readonly bool[] ButtonState = new bool[MaxButtons];
        public readonly int[] Pov = { -1, -1, -1, -1 };
    }

    private readonly object _lock = new();
    private readonly bool _skipXInput;
    private IDirectInput8? _di;
    private List<Pad> _pads = new();
    private long _nextScan;
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();

    public DirectInputProvider(bool skipXInputDevices = true)
    {
        _skipXInput = skipXInputDevices;
        try
        {
            _di = DInput.DirectInput8Create();
            Status = "DirectInput ready";
        }
        catch (Exception ex)
        {
            Status = "DirectInput unavailable: " + ex.Message;
        }
    }

    public bool IsAvailable => _di is not null;
    public string Status { get; private set; }
    public string Name => "DirectInput";
    public IReadOnlyList<DeviceInfo> Devices => _devices;

    /// <summary>"dinput:046D:C24F#1" — vendor id, product id, 1-based instance among identical devices (older ids, still accepted).</summary>
    public static string DeviceId(int vid, int pid, int instance) => $"dinput:{vid:X4}:{pid:X4}#{instance}";
    /// <summary>"dinput:046D:C24F@KEY" — KEY = USB serial number or USB port, so identical sticks never swap.</summary>
    public static string StableId(int vid, int pid, string key) => $"dinput:{vid:X4}:{pid:X4}@{key}";

    public void Poll()
    {
        if (_di is null) return;
        long now = Environment.TickCount64;
        if (now >= _nextScan) { Rescan(); _nextScan = now + 2000; }

        List<Pad> pads;
        lock (_lock) pads = _pads;
        foreach (var p in pads)
        {
            JoystickState st;
            try
            {
                if (p.Device.Poll().Failure)
                {
                    p.Device.Acquire();
                    p.Device.Poll();
                }
                st = p.Device.GetCurrentJoystickState();
            }
            catch
            {
                continue; // unplugged; the next rescan drops it
            }

            int[] raw = { st.X, st.Y, st.Z, st.RotationX, st.RotationY, st.RotationZ, st.Sliders[0], st.Sliders[1] };
            lock (_lock)
            {
                for (int a = 0; a < AxisCount; a++)
                    p.Axes[a] = p.AxisPresent[a] ? Math.Clamp(raw[a] / (double)RangeMax * 2.0 - 1.0, -1, 1) : 0;
                for (int b = 0; b < Math.Min(MaxButtons, st.Buttons.Length); b++) p.ButtonState[b] = st.Buttons[b];
                for (int v = 0; v < 4; v++) p.Pov[v] = st.PointOfViewControllers[v];
            }
        }
    }

    private void Rescan()
    {
        IList<DeviceInstance> found;
        try { found = _di!.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly); }
        catch { return; }

        List<Pad> old;
        lock (_lock) old = _pads;
        var keep = new List<Pad>();

        var seen = new HashSet<Guid>();
        foreach (var inst in found)
        {
            seen.Add(inst.InstanceGuid);
            var existing = old.FirstOrDefault(p => p.Instance == inst.InstanceGuid);
            if (existing is not null) { keep.Add(existing); continue; }

            var pad = TryOpen(inst);
            if (pad is not null) keep.Add(pad);
        }
        foreach (var gone in old.Where(p => !seen.Contains(p.Instance)))
        {
            try { gone.Device.Unacquire(); gone.Device.Dispose(); } catch { }
        }

        // Ids are VID/PID + the device's USB serial number or USB port, so identical controllers
        // keep their identity however Windows happens to list them. "#n" (in key order) remains
        // as the older id and in the name.
        foreach (var group in keep.GroupBy(p => (p.Vid, p.Pid)))
        {
            int n = 0;
            foreach (var p in group.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                p.AltId = DeviceId(p.Vid, p.Pid, ++n);
                p.DeviceId = StableId(p.Vid, p.Pid, p.Key);
            }
        }
        foreach (var group in keep.GroupBy(p => p.BaseName))
        {
            int n = 0;
            foreach (var p in group.OrderBy(p => p.Key, StringComparer.Ordinal)) p.Name = ++n == 1 ? p.BaseName : $"{p.BaseName} #{n}";
        }

        lock (_lock)
        {
            _pads = keep;
            _devices = keep.Select(p => new DeviceInfo(p.DeviceId, p.Name, Name, p.Buttons + p.Povs * 4, p.AxisPresent.Count(x => x), p.AltId)).ToList();
        }
    }

    private Pad? TryOpen(DeviceInstance inst)
    {
        IDirectInputDevice8? dev = null;
        try
        {
            dev = _di!.CreateDevice(inst.InstanceGuid);
            if (_skipXInput && IsXInputDevice(dev)) { dev.Dispose(); return null; }

            // Joysticks default to non-exclusive background access; setting it explicitly needs a
            // window handle, which we don't have in background mode, so failure here is fine.
            try { dev.SetCooperativeLevel(IntPtr.Zero, CooperativeLevel.NonExclusive | CooperativeLevel.Background); } catch { }
            dev.SetDataFormat<RawJoystickState>();

            int vid = 0, pid = 0;
            try { vid = dev.Properties.VendorId; pid = dev.Properties.ProductId; } catch { }
            var pad = new Pad { Device = dev, Instance = inst.InstanceGuid, Vid = vid, Pid = pid, BaseName = inst.ProductName.Trim() };
            string? path = null;
            try { path = dev.Properties.InterfacePath; } catch { }
            pad.Key = string.IsNullOrEmpty(path) ? "G" + DeviceKeys.Hash(inst.InstanceGuid.ToString()) : DeviceKeys.For(path).Value;
            foreach (var obj in dev.GetObjects(DeviceObjectTypeFlags.Axis))
            {
                int slot = AxisSlot(obj.ObjectType, obj.Offset);
                if (slot < 0) continue;
                pad.AxisPresent[slot] = true;
                try { dev.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(0, RangeMax); } catch { }
            }
            pad.Buttons = Math.Min(MaxButtons, dev.GetObjects(DeviceObjectTypeFlags.Button).Count);
            pad.Povs = Math.Min(4, dev.GetObjects(DeviceObjectTypeFlags.PointOfViewController).Count);
            dev.Acquire();
            return pad;
        }
        catch
        {
            dev?.Dispose();
            return null;
        }
    }

    /// <summary>XInput devices expose "IG_" in their HID interface path.</summary>
    private static bool IsXInputDevice(IDirectInputDevice8 dev)
    {
        try { return dev.Properties.InterfacePath?.Contains("IG_", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    /// <summary>Maps an axis object to our slot via its data-format offset in DIJOYSTATE2.</summary>
    private static int AxisSlot(Guid type, int offset) => offset switch
    {
        0 => 0, 4 => 1, 8 => 2, 12 => 3, 16 => 4, 20 => 5, 24 => 6, 28 => 7, // lX lY lZ lRx lRy lRz rglSlider[0..1]
        _ => -1,
    };

    public bool TryRead(InputRef input, out double value)
    {
        value = 0;
        if (!input.DeviceId.StartsWith("dinput:", StringComparison.Ordinal)) return false;
        lock (_lock)
        {
            var p = _pads.FirstOrDefault(x => (x.DeviceId == input.DeviceId || x.AltId == input.DeviceId));
            if (p is not null) value = Read(p, input.Kind, input.Index);
        }
        return true;
    }

    public IEnumerable<(InputRef Input, double Value)> ReadAll()
    {
        var result = new List<(InputRef, double)>();
        lock (_lock)
        {
            foreach (var p in _pads)
            {
                for (int b = 0; b < p.Buttons; b++)
                    result.Add((new InputRef(p.DeviceId, InputKind.Button, b, $"{p.Name} Button {b + 1}"), Read(p, InputKind.Button, b)));
                for (int v = 0; v < p.Povs; v++)
                    for (int d = 0; d < 4; d++)
                    {
                        int idx = PovBase + v * 4 + d;
                        string pov = p.Povs > 1 ? $"POV {v + 1} " : "POV ";
                        result.Add((new InputRef(p.DeviceId, InputKind.Button, idx, $"{p.Name} {pov}{PovDirs[d]}"), Read(p, InputKind.Button, idx)));
                    }
                for (int a = 0; a < AxisCount; a++)
                    if (p.AxisPresent[a])
                        result.Add((new InputRef(p.DeviceId, InputKind.Axis, a, $"{p.Name} Axis {AxisNames[a]}"), Read(p, InputKind.Axis, a)));
            }
        }
        return result;
    }

    private static double Read(Pad p, InputKind kind, int index)
    {
        switch (kind)
        {
            case InputKind.Button when index is >= 0 and < MaxButtons:
                return p.ButtonState[index] ? 1 : 0;
            case InputKind.Button when index is >= PovBase and < PovBase + 16:
            {
                int pov = p.Pov[(index - PovBase) / 4];
                if (pov < 0 || pov > 36000) return 0;
                double target = (index - PovBase) % 4 * 90.0;
                double diff = Math.Abs(((pov / 100.0 - target) % 360 + 540) % 360 - 180);
                return diff < 67.5 ? 1 : 0; // diagonals press both neighbours
            }
            case InputKind.Axis when index is >= 0 and < AxisCount:
                // DirectInput Y grows downwards; flip so "up" is positive like XInput.
                return index == 1 ? -p.Axes[index] : p.Axes[index];
            case InputKind.Trigger when index is >= 0 and < AxisCount:
                return (p.Axes[index] + 1) / 2;
            default:
                return 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var p in _pads) { try { p.Device.Unacquire(); p.Device.Dispose(); } catch { } }
            _pads.Clear();
        }
        _di?.Dispose();
        _di = null;
    }
}
