namespace UcrNext.Core;

/// <summary>Display names for outputs, plus the list of selectable outputs.</summary>
public static class OutputNames
{
    public static string Describe(OutputRef o) => o.Target switch
    {
        OutputTarget.Keyboard => "Key: " + VirtualKeys.Name(o.Index),
        OutputTarget.Mouse when o.Kind == OutputKind.Button => "Mouse button: " + (MouseButton)o.Index,
        OutputTarget.Mouse => "Mouse move: " + (MouseAxis)o.Index,
        OutputTarget.Xbox360 when o.Kind == OutputKind.Button => "Xbox 360: " + (XboxButton)o.Index,
        OutputTarget.Xbox360 => "Xbox 360: " + (XboxAxis)o.Index,
        OutputTarget.VJoy when o.Kind == OutputKind.Button => $"vJoy {o.Device}: Button {o.Index + 1}",
        OutputTarget.VJoy => $"vJoy {o.Device}: Axis {(VJoyAxis)o.Index}",
        _ => "?",
    };

    /// <summary>Every output the UI offers, virtual pads first.</summary>
    public static IEnumerable<OutputRef> All(IEnumerable<VJoyDeviceInfo>? vjoy = null)
    {
        foreach (XboxAxis a in Enum.GetValues<XboxAxis>()) yield return OutputRef.Xbox(a);
        foreach (XboxButton b in Enum.GetValues<XboxButton>()) yield return OutputRef.Xbox(b);
        foreach (var d in vjoy ?? Enumerable.Empty<VJoyDeviceInfo>())
        {
            foreach (var a in d.Axes) yield return OutputRef.VJoy(d.Id, a);
            for (int b = 1; b <= d.Buttons; b++) yield return OutputRef.VJoyButton(d.Id, b);
        }
        foreach (MouseButton b in Enum.GetValues<MouseButton>()) yield return OutputRef.Mouse(b);
        foreach (MouseAxis a in Enum.GetValues<MouseAxis>()) yield return OutputRef.Mouse(a);
        foreach (var (vk, _) in VirtualKeys.Known) yield return OutputRef.Key(vk);
    }
}

/// <summary>Win32 virtual-key names (subset covering a normal keyboard).</summary>
public static class VirtualKeys
{
    public static readonly IReadOnlyList<(int Vk, string Name)> Known = Build();

    private static readonly Dictionary<int, string> ByVk = Known.ToDictionary(k => k.Vk, k => k.Name);

    public static string Name(int vk) => ByVk.TryGetValue(vk, out var n) ? n : $"VK 0x{vk:X2}";

    private static List<(int, string)> Build()
    {
        var l = new List<(int, string)>();
        for (int c = 'A'; c <= 'Z'; c++) l.Add((c, ((char)c).ToString()));
        for (int c = '0'; c <= '9'; c++) l.Add((c, ((char)c).ToString()));
        for (int f = 1; f <= 24; f++) l.Add((0x6F + f, "F" + f));
        for (int n = 0; n <= 9; n++) l.Add((0x60 + n, "Numpad " + n));
        l.AddRange(new (int, string)[]
        {
            (0x20, "Space"), (0x0D, "Enter"), (0x1B, "Escape"), (0x09, "Tab"), (0x08, "Backspace"),
            (0x10, "Shift"), (0xA0, "Left Shift"), (0xA1, "Right Shift"),
            (0x11, "Ctrl"), (0xA2, "Left Ctrl"), (0xA3, "Right Ctrl"),
            (0x12, "Alt"), (0xA4, "Left Alt"), (0xA5, "Right Alt"),
            (0x5B, "Left Win"), (0x5C, "Right Win"), (0x5D, "Menu"),
            (0x25, "Left"), (0x26, "Up"), (0x27, "Right"), (0x28, "Down"),
            (0x2D, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"),
            (0x21, "Page Up"), (0x22, "Page Down"),
            (0x14, "Caps Lock"), (0x90, "Num Lock"), (0x91, "Scroll Lock"),
            (0x2C, "Print Screen"), (0x13, "Pause"),
            (0x6A, "Numpad *"), (0x6B, "Numpad +"), (0x6D, "Numpad -"), (0x6E, "Numpad ."), (0x6F, "Numpad /"),
            (0xBA, "; :"), (0xBB, "= +"), (0xBC, ", <"), (0xBD, "- _"), (0xBE, ". >"), (0xBF, "/ ?"),
            (0xC0, "` ~"), (0xDB, "[ {"), (0xDC, "\\ |"), (0xDD, "] }"), (0xDE, "' \""),
            (0xAD, "Volume Mute"), (0xAE, "Volume Down"), (0xAF, "Volume Up"),
            (0xB0, "Next Track"), (0xB1, "Previous Track"), (0xB2, "Stop Media"), (0xB3, "Play/Pause"),
        });
        return l;
    }
}
