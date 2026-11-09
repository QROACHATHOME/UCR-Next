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
                notes.Add($"\"{m.Title}\": input device configuration not found");
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

            AxisSettings AxisSettingsFrom(string dz = "DeadZone", string sens = "Sensitivity")
            {
                double sensitivity = D(sens, 100);
                if (sensitivity != 100 && !P("Linear"))
                    notes.Add($"\"{m.Title}\": UCR's non-linear sensitivity curve was approximated as linear");
                return new AxisSettings
                {
                    Deadzone = Math.Clamp(D(dz, 0) / 100, 0, 0.95),
                    AntiDeadzone = Math.Clamp(D("AntiDeadZone", 0) / 100, 0, 0.95),
                    Sensitivity = sensitivity <= 0 ? 1 : sensitivity / 100,
                };
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
                    if (P("CircularDz")) notes.Add($"\"{m.Title}\": circular deadzone approximated per axis");
                    Add(" (X)", In(0), Out(0), AxisSettingsFrom(), P("InvertX"));
                    Add(" (Y)", In(1), Out(1), AxisSettingsFrom(), P("InvertY"));
                    break;
                case "AxisToButton":
                {
                    double threshold = Math.Max(D("DeadZone", 30) / 100, 0.01);
                    Add(" (high)", In(0), Out(0), new AxisSettings { ButtonThreshold = threshold }, P("Invert"));
                    Add(" (low)", In(0), Out(1), new AxisSettings { ButtonThreshold = threshold }, !P("Invert"));
                    break;
                }
                case "ButtonToAxis":
                    if (P("Initialize")) notes.Add($"\"{m.Title}\": 'Initialize axis' is implicit in UCR-Next");
                    Add("", In(0), Out(0), new AxisSettings
                    {
                        ButtonAxisReleased = Math.Clamp(D("Range", 0) / 100, -1, 1),
                        ButtonAxisPressed = Math.Clamp(D("RangePressed", 100) / 100, -1, 1),
                    }, false);
                    break;
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
            notes.Add($"\"{mapping}\": output device configuration not found");
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
                    int vk = _scanToVk(sc | (e0 ? 0xE000 : 0));
                    string keyName = vk != 0 ? VirtualKeys.Name(vk) : $"SC {sc:X2}";
                    if (_perDeviceKeyboards)
                        return new InputRef($"ikbd:{hw}#{dev.Number + 1}", InputKind.Button, sc | (e0 ? 0x100 : 0), $"{dev.Title} Key {keyName}");
                    if (vk == 0) { why = $"no virtual-key code for scan code {sc:X2}"; return null; }
                    return new InputRef("keyboard", InputKind.Button, vk, $"Key {keyName}");
                }
                if (mouse && b.Type == 1 && b.Value is >= 0 and < 5)
                {
                    return _perDeviceKeyboards
                        ? new InputRef($"imouse:{hw}#{dev.Number + 1}", InputKind.Button, b.Value, $"{dev.Title} {MouseNames[b.Value]}")
                        : new InputRef("mouse", InputKind.Button, b.Value, $"Mouse {MouseNames[b.Value]}");
                }
                why = mouse ? "mouse wheel / movement inputs are not supported yet" : "unsupported keyboard binding";
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
                if (dev.Number > 0) warn = $"virtual Xbox 360 pad #{dev.Number + 1} was merged into the single UCR-Next pad";
                switch (b.Type)
                {
                    case 0 when b.Value is >= 0 and < 6: return OutputRef.Xbox((XboxAxis)b.Value);
                    case 1 when b.Value is >= 0 and < 10: return OutputRef.Xbox(VigemButtons[b.Value]);
                    case 2 when b.Value is >= 0 and < 4: return OutputRef.Xbox(VigemPov[b.Value]);
                }
                break;
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
                    int vk = _scanToVk(sc | (b.Value >= 256 ? 0xE000 : 0));
                    if (vk == 0) { why = $"no virtual-key code for scan code {sc:X2}"; return null; }
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
