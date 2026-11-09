using System.Runtime.InteropServices;
using UcrNext.Core;

namespace UcrNext.Devices;

/// <summary>Reads Xbox-compatible controllers through XInput (slots 0-3).</summary>
public sealed class XInputProvider : IInputProvider
{
    // Button index -> XINPUT_GAMEPAD bit mask, with display names.
    private static readonly (ushort Mask, string Name)[] ButtonMap =
    {
        (0x0001, "D-Pad Up"), (0x0002, "D-Pad Down"), (0x0004, "D-Pad Left"), (0x0008, "D-Pad Right"),
        (0x0010, "Start"), (0x0020, "Back"), (0x0040, "Left Stick Click"), (0x0080, "Right Stick Click"),
        (0x0100, "LB"), (0x0200, "RB"), (0x1000, "A"), (0x2000, "B"), (0x4000, "X"), (0x8000, "Y"),
    };
    private static readonly string[] AxisNames = { "Left Stick X", "Left Stick Y", "Right Stick X", "Right Stick Y" };
    private static readonly string[] TriggerNames = { "LT", "RT" };

    private readonly object _lock = new();
    private readonly XINPUT_GAMEPAD?[] _state = new XINPUT_GAMEPAD?[4];
    private readonly long[] _nextProbe = new long[4];
    private readonly Func<IReadOnlyCollection<int>> _excludedSlots;
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();

    /// <param name="excludedSlots">Slots of our own virtual pads, hidden to avoid feedback loops.</param>
    public XInputProvider(Func<IReadOnlyCollection<int>>? excludedSlots = null) =>
        _excludedSlots = excludedSlots ?? (() => Array.Empty<int>());

    public string Name => "XInput";
    public IReadOnlyList<DeviceInfo> Devices => _devices;

    public static string DeviceId(int slot) => $"xinput:{slot}";

    /// <summary>Slots (0-3) that currently have a controller, read directly from XInput.</summary>
    public static HashSet<int> ConnectedSlots()
    {
        var set = new HashSet<int>();
        for (int i = 0; i < 4; i++)
            if (XInputGetState((uint)i, out _) == 0) set.Add(i);
        return set;
    }

    public void Poll()
    {
        long now = Environment.TickCount64;
        var excluded = _excludedSlots();
        bool changed = false;
        for (int i = 0; i < 4; i++)
        {
            XINPUT_GAMEPAD? s = null;
            bool wasConnected = _state[i] is not null;
            // Polling an empty slot is slow, so only probe empty slots once a second.
            if (!excluded.Contains(i) && (wasConnected || now >= _nextProbe[i]))
            {
                if (XInputGetState((uint)i, out var st) == 0) s = st.Gamepad;
                else _nextProbe[i] = now + 1000;
            }
            lock (_lock) _state[i] = s;
            if (wasConnected != (s is not null)) changed = true;
        }
        if (changed || _devices.Count == 0 && _state.Any(x => x is not null))
        {
            var list = new List<DeviceInfo>();
            for (int i = 0; i < 4; i++)
                if (_state[i] is not null)
                    list.Add(new DeviceInfo(DeviceId(i), $"Xbox controller #{i + 1} (XInput)", Name, ButtonMap.Length, 6));
            _devices = list;
        }
    }

    public bool TryRead(InputRef input, out double value)
    {
        value = 0;
        if (!input.DeviceId.StartsWith("xinput:", StringComparison.Ordinal)) return false;
        if (!int.TryParse(input.DeviceId.AsSpan(7), out int slot) || slot is < 0 or > 3) return false;
        XINPUT_GAMEPAD? s;
        lock (_lock) s = _state[slot];
        if (s is null) return true; // known device type but disconnected: reads as 0
        value = Read(s.Value, input.Kind, input.Index);
        return true;
    }

    public IEnumerable<(InputRef Input, double Value)> ReadAll()
    {
        var result = new List<(InputRef, double)>();
        for (int slot = 0; slot < 4; slot++)
        {
            XINPUT_GAMEPAD? s;
            lock (_lock) s = _state[slot];
            if (s is null) continue;
            string id = DeviceId(slot), pad = $"XInput #{slot + 1}";
            for (int b = 0; b < ButtonMap.Length; b++)
                result.Add((new InputRef(id, InputKind.Button, b, $"{pad} {ButtonMap[b].Name}"), Read(s.Value, InputKind.Button, b)));
            for (int a = 0; a < 4; a++)
                result.Add((new InputRef(id, InputKind.Axis, a, $"{pad} {AxisNames[a]}"), Read(s.Value, InputKind.Axis, a)));
            for (int t = 0; t < 2; t++)
                result.Add((new InputRef(id, InputKind.Trigger, t, $"{pad} {TriggerNames[t]}"), Read(s.Value, InputKind.Trigger, t)));
        }
        return result;
    }

    private static double Read(XINPUT_GAMEPAD g, InputKind kind, int index) => kind switch
    {
        InputKind.Button when index < ButtonMap.Length => (g.wButtons & ButtonMap[index].Mask) != 0 ? 1 : 0,
        InputKind.Axis => index switch
        {
            0 => Norm(g.sThumbLX), 1 => Norm(g.sThumbLY), 2 => Norm(g.sThumbRX), 3 => Norm(g.sThumbRY), _ => 0,
        },
        InputKind.Trigger => index switch { 0 => g.bLeftTrigger / 255.0, 1 => g.bRightTrigger / 255.0, _ => 0 },
        _ => 0,
    };

    private static double Norm(short v) => v < 0 ? v / 32768.0 : v / 32767.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);
}
