using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UcrNext.Core;

/// <summary>
/// Imports profiles from Universal Control Remapper (evilC / Snoothy UCR, "context.xml").
///
/// UCR stores, per profile: input/output device configurations (provider name + device handle +
/// instance number), and mappings made of input DeviceBindings plus one plugin (Button to Button,
/// Axis to Axis, …) with its own output DeviceBindings and settings. A DeviceBinding is
/// KeyType (0 axis, 1 button, 2 POV), KeyValue (index) and KeySubValue (POV direction); what the
/// index means depends on the IOWrapper provider — see the converters below.
///
/// Child profiles inherit their parent's mappings (a child mapping with the same title overrides
/// the parent's), so each child is flattened into one standalone profile "Parent › Child".
/// Everything that cannot be carried over is listed in <see cref="UcrImportResult.Notes"/>.
/// Imported profiles start disabled, because UCR runs one profile at a time.
/// </summary>
public sealed class UcrImporter
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    // UCR XInput button index -> UCR-Next XInputProvider button index.
    private static readonly int[] XiButtons = { 10, 11, 12, 13, 8, 9, 6, 7, 5, 4 }; // A B X Y LB RB LS RS Back Start
    private static readonly int[] XiPov = { 0, 3, 1, 2 };                            // Up Right Down Left
    private static readonly string[] XiButtonNames = { "A", "B", "X", "Y", "LB", "RB", "LS", "RS", "Back", "Start" };
    private static readonly string[] XiAxisNames = { "LX", "LY", "RX", "RY", "LT", "RT" };
    private static readonly string[] PovNames = { "Up", "Right", "Down", "Left" };
    private static readonly string[] DiAxisNames = { "X", "Y", "Z", "Rx", "Ry", "Rz", "Slider 1", "Slider 2" };
    private static readonly XboxButton[] VigemButtons =
    {
        XboxButton.A, XboxButton.B, XboxButton.X, XboxButton.Y, XboxButton.LeftShoulder, XboxButton.RightShoulder,
        XboxButton.LeftThumb, XboxButton.RightThumb, XboxButton.Back, XboxButton.Start,
    };
    private static readonly XboxButton[] VigemPov = { XboxButton.DPadUp, XboxButton.DPadRight, XboxButton.DPadDown, XboxButton.DPadLeft };
    private static readonly string[] MouseNames = { "Left", "Right", "Middle", "X1", "X2" };
    private static readonly string[] WheelNames = { "Wheel Up", "Wheel Down", "Wheel Right", "Wheel Left" };

    private const int DiButtonsOffset = 48; // SharpDX JoystickOffset.Buttons0
    private const int DiPovBase = 128;      // UCR-Next DirectInputProvider.PovBase

    private readonly Func<int, int> _scanToVk;
    private readonly bool _perDeviceKeyboards;

    /// <param name="scanToVk">Scan code (0xE0xx for extended keys) to Win32 virtual-key code; 0 if unknown.</param>
    /// <param name="perDeviceKeyboards">
    /// Keep UCR's per-keyboard/per-mouse (Interception) inputs per device. If false they become
    /// "any keyboard / any mouse" bindings, which work without the Interception driver.
    /// </param>
    public UcrImporter(Func<int, int> scanToVk, bool perDeviceKeyboards)
    {
        _scanToVk = scanToVk;
        _perDeviceKeyboards = perDeviceKeyboards;
    }

    public UcrImportResult ImportFile(string path) => Import(XDocument.Load(path));

    public UcrImportResult Import(XDocument doc)
    {
        var result = new UcrImportResult();
        var profilesEl = doc.Root?.Element("Profiles");
        if (doc.Root?.Name.LocalName != "Context" || profilesEl is null)
            throw new FormatException("This is not a UCR context.xml file (no <Context><Profiles>).");

        foreach (var p in profilesEl.Elements("Profile"))
            ImportProfile(p, parent: null, result);
        return result;
    }

    private sealed record UcrDevice(string Title, string Provider, string Handle, int Number);
    private sealed record UcrMapping(string Title, XElement Element, string Owner);
    private sealed record Scope(string Name, Dictionary<Guid, UcrDevice> Inputs, Dictionary<Guid, UcrDevice> Outputs, List<UcrMapping> Mappings);

    private void ImportProfile(XElement el, Scope? parent, UcrImportResult result)
    {
        string title = (string?)el.Attribute("Title") ?? "Imported profile";
        string name = parent is null ? title : $"{parent.Name} › {title}";

        var inputs = new Dictionary<Guid, UcrDevice>(parent?.Inputs ?? new());
        var outputs = new Dictionary<Guid, UcrDevice>(parent?.Outputs ?? new());
        foreach (var (guid, dev) in ReadDevices(el.Element("InputDeviceConfigurations"))) inputs[guid] = dev;
        foreach (var (guid, dev) in ReadDevices(el.Element("OutputDeviceConfigurations"))) outputs[guid] = dev;

        // Child mappings override parent mappings with the same title.
        var own = (el.Element("Mappings")?.Elements("Mapping") ?? Enumerable.Empty<XElement>())
            .Select(m => new UcrMapping((string?)m.Attribute("Title") ?? "Mapping", m, name)).ToList();
        var ownTitles = own.Select(m => m.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings = (parent?.Mappings ?? new List<UcrMapping>()).Where(m => !ownTitles.Contains(m.Title)).Concat(own).ToList();
        var scope = new Scope(name, inputs, outputs, mappings);

        var profile = new Profile
        {
            Id = Guid.TryParse((string?)el.Attribute("Guid"), out var g) ? g : Guid.NewGuid(),
            Name = name,
            Enabled = false,
        };
        var notes = new List<string>();
        foreach (var m in mappings) ConvertMapping(m, scope, profile, notes);

        if (el.Descendants("ShadowDevices").Any(s => s.HasElements))
            notes.Add("Shadow devices are not supported; only the main device of each configuration was used.");

        result.Profiles.Add(profile);
        result.Notes.Add($"{name}: {profile.Bindings.Count} binding(s) imported" + (notes.Count == 0 ? "" : $", {notes.Count} note(s)"));
        result.Notes.AddRange(notes.Distinct().Select(n => "    - " + n));

        foreach (var child in el.Element("ChildProfiles")?.Elements("Profile") ?? Enumerable.Empty<XElement>())
            ImportProfile(child, scope, result);
    }

    private static IEnumerable<(Guid, UcrDevice)> ReadDevices(XElement? list)
    {
        if (list is null) yield break;
        foreach (var dc in list.Elements("DeviceConfiguration"))
        {
            var d = dc.Element("Device");
            if (d is null || !Guid.TryParse((string?)dc.Attribute("Guid"), out var guid)) continue;
            yield return (guid, new UcrDevice(
                (string?)d.Attribute("Title") ?? "",
                (string?)d.Attribute("ProviderName") ?? "",
                (string?)d.Attribute("DeviceHandle") ?? "",
                int.TryParse((string?)d.Attribute("DeviceNumber"), out var n) ? n : 0));
        }
    }

    private sealed record UcrBinding(bool IsBound, Guid Config, int Type, int Value, int Sub, bool Block);

    private static UcrBinding ReadBinding(XElement b) => new(
        Bool(b.Attribute("IsBound")?.Value),
        Guid.TryParse((string?)b.Attribute("DeviceConfigurationGuid"), out var g) ? g : Guid.Empty,
        Int(b.Attribute("KeyType")?.Value), Int(b.Attribute("KeyValue")?.Value), Int(b.Attribute("KeySubValue")?.Value),
        Bool(b.Attribute("Block")?.Value));

    private void ConvertMapping(UcrMapping m, Scope scope, Profile profile, List<string> notes)
    {
        var plugins = m.Element.Element("Plugins")?.Elements().ToList() ?? new List<XElement>();
        var inputBindings = m.Element.Element("DeviceBindings")?.Elements("DeviceBinding").Select(ReadBinding).ToList() ?? new();

        // Resolve the mapping's inputs once (index = plugin input slot).
        var inputs = new List<(InputRef? Ref, bool FlipY, bool Block)>();
        foreach (var b in inputBindings)
        {
            if (!b.IsBound) { inputs.Add((null, false, false)); continue; }
            if (!scope.Inputs.TryGetValue(b.Config, out var dev))
            {
                notes.Add($"\"{m.Title}\": an input refers to a device configuration that no longer exists in this UCR profile (it didn't work in UCR either)");
                inputs.Add((null, false, false));
                continue;
            }
            var r = ConvertInput(dev, b, out bool flip, out string? why);
            if (r is null) notes.Add($"\"{m.Title}\": input skipped — {why}");
            inputs.Add((r, flip, b.Block));
        }

        foreach (var plugin in plugins)
        {
            string type = PluginType(plugin);
            var outBindings = plugin.Element("Outputs")?.Elements("DeviceBinding").Select(ReadBinding).ToList() ?? new();
            var outputs = outBindings.Select(b => b.IsBound ? ResolveOutput(b, scope, m.Title, notes) : null).ToList();
            if (plugin.Element("Filters")?.HasElements == true) notes.Add($"\"{m.Title}\": plugin filters are not supported and were ignored");

            (InputRef? Ref, bool FlipY, bool Block) In(int i) => i < inputs.Count ? inputs[i] : (null, false, false);
            OutputRef? Out(int i) => i < outputs.Count ? outputs[i] : null;
            bool P(string n) => Bool(plugin.Element(n)?.Value);
            double D(string n, double def) =>
                double.TryParse(plugin.Element(n)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

            string S(string n, string def) => plugin.Element(n)?.Value ?? def;
            bool Flip(int i) => In(i).FlipY && In(i).Ref?.Kind == InputKind.Axis;

            // Direct binding (one input → one output).
            void Add(string suffix, (InputRef? Ref, bool FlipY, bool Block) input, OutputRef? output, AxisSettings settings, bool invert)
            {
                if (input.Ref is null || output is null) return;
                settings.Invert = invert ^ (input.FlipY && input.Ref.Kind == InputKind.Axis);
                if (output.IsTrigger && input.Ref.Kind == InputKind.Axis) settings.TriggerFullRange = true; // UCR maps the whole axis onto 0..255
                profile.Bindings.Add(new Binding
                {
                    Name = m.Title + suffix,
                    Input = input.Ref,
                    Output = output,
                    Settings = settings,
                    Block = input.Block && IsBlockable(input.Ref),
                });
            }

            // Multi-input / stateful binding. Settings are used as given (flips already applied by the caller).
            void AddMode(string suffix, BindingMode mode, int primary, int[] extras, OutputRef? output, AxisSettings settings)
            {
                var input = In(primary);
                if (input.Ref is null || output is null) return;
                profile.Bindings.Add(new Binding
                {
                    Name = m.Title + suffix,
                    Mode = mode,
                    Input = input.Ref,
                    ExtraInputs = extras.Select(i => In(i).Ref).ToList(),
                    Output = output,
                    Settings = settings,
                    Block = input.Block && IsBlockable(input.Ref),
                });
            }

            AxisSettings AxisSettingsFrom(string dz = "DeadZone", string sens = "Sensitivity")
            {
                double sensitivity = D(sens, 100);
                return new AxisSettings
                {
                    Deadzone = Math.Clamp(D(dz, 0) / 100, 0, 0.95),
                    AntiDeadzone = Math.Clamp(D("AntiDeadZone", 0) / 100, 0, 0.95),
                    Sensitivity = sensitivity <= 0 ? 1 : sensitivity / 100,
                    // UCR only applies its curve when sensitivity isn't 100 %; Linear = plain multiplier.
                    NonLinearSensitivity = sensitivity != 100 && !P("Linear"),
                };
            }

            AxisSettings PairSettings(PairComponent c, double rotation = 0)
            {
                var s = AxisSettingsFrom();
                s.AntiDeadzone = 0;
                s.CircularDeadzone = P("CircularDz");
                s.Component = c;
                s.Invert = P("InvertX") ^ Flip(0);
                s.InvertY = P("InvertY") ^ Flip(1);
                s.RotationDegrees = rotation;
                return s;
            }

            switch (type)
            {
                case "ButtonToButton":
                    Add("", In(0), Out(0), new AxisSettings(), P("Invert"));
                    break;
                case "AxisToAxis":
                    Add("", In(0), Out(0), AxisSettingsFrom(), P("Invert"));
                    break;
                case "AxesToAxes":
                    AddMode(" (X)", BindingMode.StickPair, 0, new[] { 1 }, Out(0), PairSettings(PairComponent.X));
                    AddMode(" (Y)", BindingMode.StickPair, 0, new[] { 1 }, Out(1), PairSettings(PairComponent.Y));
                    break;
                case "AxesToAxesRotation":
                    AddMode(" (X)", BindingMode.StickPair, 0, new[] { 1 }, Out(0), PairSettings(PairComponent.X, D("Rotation", 0)));
                    AddMode(" (Y)", BindingMode.StickPair, 0, new[] { 1 }, Out(1), PairSettings(PairComponent.Y, D("Rotation", 0)));
                    break;
                case "AxesToAxesTrim":
                    AddMode(" (X)", BindingMode.StickTrim, 0, new[] { 1, 2, 3 }, Out(0), PairSettings(PairComponent.X));
                    AddMode(" (Y)", BindingMode.StickTrim, 0, new[] { 1, 2, 3 }, Out(1), PairSettings(PairComponent.Y));
                    break;
                case "AxisToButton":
                {
                    double threshold = Math.Max(D("DeadZone", 30) / 100, 0.01);
                    Add(" (high)", In(0), Out(0), new AxisSettings { ButtonThreshold = threshold }, P("Invert"));
                    Add(" (low)", In(0), Out(1), new AxisSettings { ButtonThreshold = threshold }, !P("Invert"));
                    break;
                }
                case "AxesRangeToButton":
                    AddMode("", BindingMode.AxesRange, 0, new[] { 1 }, Out(0), new AxisSettings
                    {
                        Invert = P("InvertX") ^ Flip(0),
                        InvertY = P("InvertY") ^ Flip(1),
                        XStart = D("XStart", -50) / 100, XEnd = D("XEnd", 50) / 100,
                        YStart = D("YStart", -50) / 100, YEnd = D("YEnd", 50) / 100,
                    });
                    break;
                case "ButtonToAxis":
                    Add("", In(0), Out(0), new AxisSettings
                    {
                        ButtonAxisReleased = Math.Clamp(D("Range", 0) / 100, -1, 1),
                        ButtonAxisPressed = Math.Clamp(D("RangePressed", 100) / 100, -1, 1),
                    }, false);
                    break;
                case "ButtonsToAxis":
                    // UCR: first input alone → axis max, second alone → axis min, both/none → centre.
                    AddMode("", BindingMode.ButtonsToAxis, 0, new[] { 1 }, Out(0), new AxisSettings { Invert = P("Invert") });
                    break;
                case "AxisSplitter":
                {
                    // A flipped input (DirectInput Y) swaps which half is "high".
                    bool f = Flip(0);
                    var dz = Math.Clamp(D("DeadZone", 0) / 100, 0, 0.95);
                    AddMode(" (high)", BindingMode.Direct, 0, Array.Empty<int>(), Out(0),
                        new AxisSettings { Deadzone = dz, Half = f ? AxisHalf.Low : AxisHalf.High, Invert = P("InvertHigh") });
                    AddMode(" (low)", BindingMode.Direct, 0, Array.Empty<int>(), Out(1),
                        new AxisSettings { Deadzone = dz, Half = f ? AxisHalf.High : AxisHalf.Low, Invert = P("InvertLow") });
                    break;
                }
                case "DeltaToAxis":
                    AddMode("", BindingMode.DeltaToAxis, 0, new[] { 1 }, Out(0), new AxisSettings
                    {
                        DeltaMode = S("Mode", "Absolute") == "Relative" ? DeltaMode.Relative : DeltaMode.Absolute,
                        DeltaSensitivity = D("Sensitivity", 100),
                        DeltaDeadzone = (int)D("Deadzone", 0),
                        TimeoutMs = (int)D("AbsoluteTimeout", 100),
                    });
                    break;
                case "DeltaToButtons":
                {
                    AxisSettings Dir(int d) => new()
                    {
                        Direction = d,
                        DeltaDeadzone = (int)Math.Abs(D("Min", 0)),
                        TimeoutMs = (int)D("CenterTimeout", 100),
                        DebounceMs = (int)D("DeBounceTimeout", 100),
                    };
                    AddMode(" (low)", BindingMode.DeltaToButtons, 0, Array.Empty<int>(), Out(0), Dir(-1));
                    AddMode(" (high)", BindingMode.DeltaToButtons, 0, Array.Empty<int>(), Out(1), Dir(1));
                    break;
                }
                case "ButtonToButtonsLongPress":
                {
                    AxisSettings Lp(PairComponent c) => new()
                    {
                        Component = c,
                        LongPressMs = (int)D("Timeout", 200),
                        ShortPressMs = (int)D("ShortPressDuration", 50),
                    };
                    AddMode(" (short)", BindingMode.LongPress, 0, Array.Empty<int>(), Out(0), Lp(PairComponent.X));
                    AddMode(" (long)", BindingMode.LongPress, 0, Array.Empty<int>(), Out(1), Lp(PairComponent.Y));
                    break;
                }
                case "AxisToAxisIncrement":
                {
                    if (!P("RelativeContinue") && plugin.Element("RelativeContinue") is not null)
                        notes.Add($"\"{m.Title}\": 'Continue Mode' off is treated as on (the output keeps moving while the axis is held)");
                    double sens = D("Sensitivity", 100);
                    AddMode("", BindingMode.Increment, 0, Array.Empty<int>(), Out(0), new AxisSettings
                    {
                        Invert = P("Invert") ^ Flip(0),
                        Deadzone = Math.Clamp(D("DeadZone", 0) / 100, 0, 0.95),
                        Sensitivity = sens <= 0 ? 1 : sens / 100,
                        NonLinearSensitivity = sens != 100 && !P("Linear"),
                        IncrementRate = D("RelativeSensitivity", 2),
                        CounterEffect = D("CounterEffect", 1.5),
                    });
                    break;
                }
                default:
                    notes.Add($"\"{m.Title}\": plugin \"{type}\" is not supported yet");
                    break;
            }
        }
    }

    private OutputRef? ResolveOutput(UcrBinding b, Scope scope, string mapping, List<string> notes)
    {
        if (!scope.Outputs.TryGetValue(b.Config, out var dev))
        {
            notes.Add($"\"{mapping}\": an output refers to a device configuration that no longer exists in this UCR profile (it didn't work in UCR either)");
            return null;
        }
        var r = ConvertOutput(dev, b, out string? why, out string? warn);
        if (r is null) notes.Add($"\"{mapping}\": output skipped — {why}");
        if (warn is not null) notes.Add($"\"{mapping}\": {warn}");
        return r;
    }

    // ---- Inputs ----

    private InputRef? ConvertInput(UcrDevice dev, UcrBinding b, out bool flipY, out string? why)
    {
        flipY = false;
        why = null;
        switch (dev.Provider)
        {
            case "SharpDX_XInput":
            {
                string id = $"xinput:{dev.Number}";
                string pad = $"XInput #{dev.Number + 1}";
                switch (b.Type)
                {
                    case 1 when b.Value is >= 0 and < 10:
                        return new InputRef(id, InputKind.Button, XiButtons[b.Value], $"{pad} {XiButtonNames[b.Value]}");
                    case 0 when b.Value is >= 0 and < 4:
                        return new InputRef(id, InputKind.Axis, b.Value, $"{pad} {XiAxisNames[b.Value]}");
                    case 0 when b.Value is 4 or 5:
                        return new InputRef(id, InputKind.Trigger, b.Value - 4, $"{pad} {XiAxisNames[b.Value]}");
                    case 2 when b.Sub is >= 0 and < 4:
                        return new InputRef(id, InputKind.Button, XiPov[b.Sub], $"{pad} D-Pad {PovNames[b.Sub]}");
                }
                break;
            }
            case "SharpDX_DirectInput":
            {
                if (!TryVidPid(dev.Handle, out int vid, out int pid)) { why = $"unrecognised DirectInput handle \"{dev.Handle}\""; return null; }
                string id = $"dinput:{vid:X4}:{pid:X4}#{dev.Number + 1}";
                string name = string.IsNullOrEmpty(dev.Title) ? $"{vid:X4}:{pid:X4}" : dev.Title;
                switch (b.Type)
                {
                    case 0 when b.Value is >= 0 and <= 28 && b.Value % 4 == 0:
                    {
                        int slot = b.Value / 4;
                        flipY = slot == 1; // UCR passes DirectInput Y raw (down = positive); UCR-Next reports up = positive
                        return new InputRef(id, InputKind.Axis, slot, $"{name} Axis {DiAxisNames[slot]}");
                    }
                    case 1 when b.Value >= DiButtonsOffset && b.Value < DiButtonsOffset + 128:
                        return new InputRef(id, InputKind.Button, b.Value - DiButtonsOffset, $"{name} Button {b.Value - DiButtonsOffset + 1}");
                    case 2 when b.Value is >= 0 and < 4 && b.Sub is >= 0 and < 4:
                        return new InputRef(id, InputKind.Button, DiPovBase + b.Value * 4 + b.Sub, $"{name} POV {PovNames[b.Sub]}");
                }
                break;
            }
            case "Core_Interception":
            {
                bool keyboard = dev.Handle.StartsWith(@"Keyboard\", StringComparison.OrdinalIgnoreCase);
                bool mouse = dev.Handle.StartsWith(@"Mouse\", StringComparison.OrdinalIgnoreCase);
                string hw = keyboard || mouse ? dev.Handle[(dev.Handle.IndexOf('\\') + 1)..] : dev.Handle;
                if (keyboard && b.Type == 1 && b.Value is >= 0 and < 512)
                {
                    int sc = b.Value % 256 + 1;
                    bool e0 = b.Value >= 256;
                    int vk = ScanToVk(sc, e0);
                    string keyName = vk != 0 ? VirtualKeys.Name(vk) : $"SC {(e0 ? "E0 " : "")}{sc:X2}";
                    if (_perDeviceKeyboards)
                        return new InputRef($"ikbd:{hw}#{dev.Number + 1}", InputKind.Button, sc | (e0 ? 0x100 : 0), $"{dev.Title} Key {keyName}");
                    if (vk == 0) { why = $"no virtual-key code for scan code {(e0 ? "E0 " : "")}{sc:X2}"; return null; }
                    return new InputRef("keyboard", InputKind.Button, vk, $"Key {keyName}");
                }
                if (mouse)
                {
                    // Per-device mouse movement/wheel comes from Raw Input (no driver needed), buttons from Interception.
                    string rawId = TryVidPid(hw, out int mv, out int mp) ? $"rawmouse:{mv:X4}:{mp:X4}#{dev.Number + 1}" : "mouse";
                    string anyOrRaw = _perDeviceKeyboards ? rawId : "mouse";
                    string who = _perDeviceKeyboards ? dev.Title : "Mouse";
                    switch (b.Type)
                    {
                        case 1 when b.Value is >= 0 and < 5:
                            return _perDeviceKeyboards
                                ? new InputRef($"imouse:{hw}#{dev.Number + 1}", InputKind.Button, b.Value, $"{dev.Title} {MouseNames[b.Value]}")
                                : new InputRef("mouse", InputKind.Button, b.Value, $"Mouse {MouseNames[b.Value]}");
                        case 1 when b.Value is >= 5 and < 9: // Wheel Up, Down, Right, Left
                            return new InputRef(anyOrRaw, InputKind.Button, b.Value, $"{who} {WheelNames[b.Value - 5]}");
                        case 0 when b.Value is 0 or 1:      // movement X / Y
                            return new InputRef(anyOrRaw, InputKind.Delta, b.Value, $"{who} movement {(b.Value == 0 ? "X" : "Y")}");
                    }
                    why = $"unsupported mouse binding (type {b.Type}, index {b.Value})";
                    return null;
                }
                why = $"unsupported keyboard binding (type {b.Type}, index {b.Value})";
                return null;
            }
            default:
                why = $"input provider \"{dev.Provider}\" is not supported";
                return null;
        }
        why = $"unsupported {dev.Provider} binding (type {b.Type}, index {b.Value})";
        return null;
    }

    // ---- Outputs ----

    private OutputRef? ConvertOutput(UcrDevice dev, UcrBinding b, out string? why, out string? warn)
    {
        why = null;
        warn = null;
        switch (dev.Provider)
        {
            case "Core_ViGEm":
                if (!dev.Handle.Equals("xb360", StringComparison.OrdinalIgnoreCase))
                {
                    why = $"ViGEm \"{dev.Handle}\" (e.g. DualShock 4) output is not supported; only Xbox 360";
                    return null;
                }
            {
                int pad = dev.Number;
                if (pad is < 0 or >= OutputRef.MaxXboxPads)
                {
                    warn = $"virtual Xbox 360 pad #{pad + 1} doesn't exist (max {OutputRef.MaxXboxPads}); used pad #{OutputRef.MaxXboxPads}";
                    pad = Math.Clamp(pad, 0, OutputRef.MaxXboxPads - 1);
                }
                switch (b.Type)
                {
                    case 0 when b.Value is >= 0 and < 6: return OutputRef.Xbox((XboxAxis)b.Value, pad);
                    case 1 when b.Value is >= 0 and < 10: return OutputRef.Xbox(VigemButtons[b.Value], pad);
                    case 2 when b.Value is >= 0 and < 4: return OutputRef.Xbox(VigemPov[b.Value], pad);
                }
                break;
            }
            case "Core_vJoyInterfaceWrap":
            {
                if (!int.TryParse(dev.Handle, out int h)) { why = $"unrecognised vJoy handle \"{dev.Handle}\""; return null; }
                int device = h + 1;
                switch (b.Type)
                {
                    case 0 when b.Value is >= 0 and < 8: return OutputRef.VJoy(device, (VJoyAxis)b.Value);
                    case 1 when b.Value is >= 0 and < VJoyState.MaxButtons: return OutputRef.VJoyButton(device, b.Value + 1);
                    case 2: why = "vJoy POV hat output is not supported yet"; return null;
                }
                break;
            }
            case "Core_Interception":
            {
                bool keyboard = dev.Handle.StartsWith(@"Keyboard\", StringComparison.OrdinalIgnoreCase);
                if (keyboard && b.Type == 1 && b.Value is >= 0 and < 512)
                {
                    int sc = b.Value % 256 + 1;
                    bool e0 = b.Value >= 256;
                    int vk = ScanToVk(sc, e0);
                    if (vk == 0) { why = $"no virtual-key code for scan code {(e0 ? "E0 " : "")}{sc:X2}"; return null; }
                    return OutputRef.Key(vk);
                }
                if (!keyboard && b.Type == 1 && b.Value is >= 0 and < 5) return OutputRef.Mouse((MouseButton)b.Value);
                if (!keyboard && b.Type == 0 && b.Value is 0 or 1)
                {
                    warn = "mouse movement speed may differ from UCR";
                    return OutputRef.Mouse((MouseAxis)b.Value);
                }
                why = "mouse wheel output is not supported yet";
                return null;
            }
            default:
                why = $"output provider \"{dev.Provider}\" is not supported";
                return null;
        }
        why = $"unsupported {dev.Provider} output (type {b.Type}, index {b.Value})";
        return null;
    }

    // ---- helpers ----

    private static string PluginType(XElement plugin)
    {
        // XmlSerializer writes derived plugins as <Plugin xsi:type="ButtonToButton">.
        var t = (string?)plugin.Attribute(Xsi + "type") ?? plugin.Name.LocalName;
        int colon = t.LastIndexOf(':');
        return colon >= 0 ? t[(colon + 1)..] : t;
    }

    /// <summary>
    /// Scan code → virtual key, with fallbacks for keys the layout doesn't map directly:
    /// an extended code is retried without E0, and the numeric keypad has fixed codes.
    /// </summary>
    private int ScanToVk(int sc, bool e0)
    {
        int vk = _scanToVk(sc | (e0 ? 0xE000 : 0));
        if (vk == 0 && e0) vk = _scanToVk(sc);
        if (vk == 0 && !e0) vk = sc switch
        {
            0x37 => 0x6A, 0x4A => 0x6D, 0x4E => 0x6B, 0x53 => 0x6E,          // Numpad * - + .
            0x47 => 0x67, 0x48 => 0x68, 0x49 => 0x69, 0x4B => 0x64, 0x4C => 0x65,
            0x4D => 0x66, 0x4F => 0x61, 0x50 => 0x62, 0x51 => 0x63, 0x52 => 0x60, // Numpad 7 8 9 4 5 6 1 2 3 0
            _ => 0,
        };
        if (vk == 0 && e0 && sc == 0x35) vk = 0x6F;                          // Numpad /
        return vk;
    }

    private static bool IsBlockable(InputRef r) =>
        r.DeviceId is "keyboard" or "mouse" || r.DeviceId.StartsWith("ikbd:") || r.DeviceId.StartsWith("imouse:");

    private static bool TryVidPid(string handle, out int vid, out int pid)
    {
        vid = pid = 0;
        var m = Regex.Match(handle, @"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        vid = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
        pid = int.Parse(m.Groups[2].Value, NumberStyles.HexNumber);
        return true;
    }

    private static bool Bool(string? s) => s is not null && bool.TryParse(s, out var b) && b;
    private static int Int(string? s) => int.TryParse(s, out var i) ? i : 0;
}

public sealed class UcrImportResult
{
    public List<Profile> Profiles { get; } = new();
    /// <summary>Human-readable report: one line per profile, then indented notes about what was approximated or skipped.</summary>
    public List<string> Notes { get; } = new();
    public string Report => string.Join(Environment.NewLine, Notes);

    /// <summary>
    /// Saves the imported profiles. Re-importing the same UCR profile (same GUID) replaces its
    /// bindings but keeps what was added in UCR-Next: games, programs, device rules, enabled state.
    /// </summary>
    public void SaveTo(ProfileStore store)
    {
        var existing = store.LoadAll().ToDictionary(p => p.Id);
        foreach (var p in Profiles)
        {
            if (existing.TryGetValue(p.Id, out var old))
            {
                p.Enabled = old.Enabled;
                p.Applications = old.Applications;
                p.Games = old.Games;
                p.SkipIfDevicesPresent = old.SkipIfDevicesPresent;
            }
            store.Save(p);
        }
    }

    /// <summary>Default context.xml locations to offer in the file dialog.</summary>
    public static string DefaultHint => "context.xml in the folder where UCR.exe is";
}
