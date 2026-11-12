using System.Text.Json;
using System.Text.Json.Serialization;

namespace UcrNext.Core;

/// <summary>Kind of a control on the arcade panel (same names as LED-Next, so its layouts import as they are).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PanelControlKind { Button, Start, Coin, Admin, Trackball, Spinner, Other, Joystick }

/// <summary>Which input one part of a panel control is (a joystick has four parts, a trackball two).</summary>
public sealed class PanelLink
{
    /// <summary>"Press", "Up", "Down", "Left", "Right", "Turn", "X", "Y".</summary>
    public string Part { get; set; } = "Press";
    public InputRef Input { get; set; } = new();
    /// <summary>For an axis: the direction that counts as this part (+1 or -1). 0 = any change.</summary>
    public int Sign { get; set; }
}

/// <summary>One control on the panel. X/Y are its centre, in panel units.</summary>
public sealed class PanelControl
{
    /// <summary>Stable name, e.g. "P1_B1", "P2_START" (LED-Next uses the same ids).</summary>
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>1-4, or 0 for shared controls.</summary>
    public int Player { get; set; }
    public PanelControlKind Kind { get; set; } = PanelControlKind.Button;
    public double X { get; set; }
    public double Y { get; set; }
    public double Size { get; set; } = 56;
    /// <summary>The inputs this control produces, one per part.</summary>
    public List<PanelLink> Links { get; set; } = new();

    /// <summary>The parts a control of this kind has, in the order they are linked.</summary>
    public static string[] PartsOf(PanelControlKind kind) => kind switch
    {
        PanelControlKind.Joystick => new[] { "Up", "Down", "Left", "Right" },
        PanelControlKind.Spinner => new[] { "Turn" },
        PanelControlKind.Trackball => new[] { "X", "Y" },
        _ => new[] { "Press" },
    };

    [JsonIgnore] public string[] Parts => PartsOf(Kind);
    public PanelLink? Link(string part) => Links.FirstOrDefault(l => l.Part == part);
    /// <summary>Every part has an input.</summary>
    [JsonIgnore] public bool IsLinked => Parts.All(p => Link(p) is not null);

    public PanelControl Clone() => JsonSerializer.Deserialize<PanelControl>(JsonSerializer.Serialize(this))!;

    /// <summary>
    /// True if a part of this control is active for <paramref name="value"/>: a pressed button,
    /// a stick pushed past half way in the part's direction, or any movement of a spinner/trackball.
    /// </summary>
    public static bool IsActive(PanelLink link, double value) => link.Input.Kind switch
    {
        InputKind.Delta => value != 0,
        InputKind.Axis when link.Sign != 0 => value * link.Sign >= 0.5,
        InputKind.Axis => Math.Abs(value) >= 0.5,
        _ => value >= 0.5,
    };
}

/// <summary>The arcade control panel as it looks: a photo and the controls on it, stored in %APPDATA%\UCR-Next\panel.json.</summary>
public sealed class PanelLayout
{
    public double Width { get; set; } = 1600;
    public double Height { get; set; } = 640;
    /// <summary>Picture file name in the panel-images folder; null = no photo.</summary>
    public string? BackgroundImage { get; set; }
    public double BackgroundOpacity { get; set; } = 0.6;
    public List<PanelControl> Controls { get; set; } = new();

    public PanelControl? Find(string id) => Controls.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// The folder the panel is kept in: the one that holds the profiles folder
    /// (%APPDATA%\UCR-Next normally; a test copy started with --profiles keeps its own).
    /// </summary>
    public static string FolderFor(string profilesDirectory) =>
        Path.GetDirectoryName(Path.GetFullPath(profilesDirectory).TrimEnd('\\', '/')) ?? profilesDirectory;

    public static string PathIn(string folder) => Path.Combine(folder, "panel.json");
    public static string ImagesIn(string folder) => Path.Combine(folder, "panel-images");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static PanelLayout Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<PanelLayout>(File.ReadAllText(path), Options) ?? new();
        }
        catch (JsonException) { /* fall back to an empty panel */ }
        return new PanelLayout();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static PanelLayout FromJson(string json) => JsonSerializer.Deserialize<PanelLayout>(json, Options) ?? new();

    /// <summary>An id no control uses yet: bumps a trailing number ("P1_B8" → "P1_B9") or appends one.</summary>
    public string NextIdAfter(string sourceId)
    {
        int digits = sourceId.Length - sourceId.TrimEnd("0123456789".ToCharArray()).Length;
        if (digits > 0)
        {
            var prefix = sourceId[..^digits];
            for (long n = long.Parse(sourceId[^digits..]) + 1; ; n++)
                if (Find($"{prefix}{n}") is null) return $"{prefix}{n}";
        }
        for (int i = 2; ; i++)
            if (Find($"{sourceId}_{i}") is null) return $"{sourceId}_{i}";
    }

    /// <summary>
    /// Reads LED-Next's cabinet.json (its "panel": size, photo, buttons with id/label/player/kind/x/y/size).
    /// Links are not in LED-Next's file. Returns the layout and the full path of its photo, if any.
    /// </summary>
    public static (PanelLayout Layout, string? PhotoPath) ImportLedNext(string cabinetJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(cabinetJsonPath));
        var root = doc.RootElement;
        var panel = Get(root, "panel") ?? throw new FormatException("This file has no \"panel\" (expected LED-Next's cabinet.json).");
        var layout = new PanelLayout
        {
            Width = Num(panel, "width", 1600),
            Height = Num(panel, "height", 640),
            BackgroundOpacity = Num(panel, "backgroundOpacity", 0.6),
        };
        string? photo = null;
        if (Get(panel, "backgroundImage") is { ValueKind: JsonValueKind.String } bg && bg.GetString() is { Length: > 0 } file)
        {
            var full = Path.IsPathRooted(file) ? file : Path.Combine(Path.GetDirectoryName(cabinetJsonPath)!, file);
            if (File.Exists(full)) photo = full;
        }
        if (Get(panel, "buttons") is { ValueKind: JsonValueKind.Array } buttons)
            foreach (var b in buttons.EnumerateArray())
            {
                var c = new PanelControl
                {
                    Id = Str(b, "id"),
                    Label = Str(b, "label"),
                    Player = (int)Num(b, "player", 0),
                    X = Num(b, "x", 0),
                    Y = Num(b, "y", 0),
                    Size = Num(b, "size", 56),
                };
                if (Enum.TryParse<PanelControlKind>(Str(b, "kind"), true, out var k)) c.Kind = k;
                else if (Get(b, "kind") is { ValueKind: JsonValueKind.Number } kn && kn.TryGetInt32(out int ki) && Enum.IsDefined((PanelControlKind)ki)) c.Kind = (PanelControlKind)ki;
                if (c.Id.Length == 0) c.Id = layout.NextIdAfter("CONTROL");
                if (layout.Find(c.Id) is null) layout.Controls.Add(c);
            }
        return (layout, photo);

        static JsonElement? Get(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in e.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }
        static double Num(JsonElement e, string name, double def) =>
            Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : def;
        static string Str(JsonElement e, string name) =>
            Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
    }

    /// <summary>
    /// Takes positions, sizes, names and photo from <paramref name="imported"/> and keeps the links
    /// already made for controls with the same id (so re-importing after moving buttons in LED-Next is safe).
    /// </summary>
    public void MergeFrom(PanelLayout imported)
    {
        var old = Controls.ToDictionary(c => c.Id);
        Width = imported.Width;
        Height = imported.Height;
        BackgroundOpacity = imported.BackgroundOpacity;
        Controls = imported.Controls.Select(c =>
        {
            if (old.TryGetValue(c.Id, out var prev)) c.Links = prev.Links.Where(l => PanelControl.PartsOf(c.Kind).Contains(l.Part)).ToList();
            return c;
        }).ToList();
    }
}

/// <summary>What a profile does with the controls on the panel.</summary>
public static class PanelMapping
{
    /// <summary>
    /// The bindings of <paramref name="profile"/> that read one of the control's linked inputs (main or extra input),
    /// each with the parts of the control it covers, in profile order. <paramref name="resolve"/> turns a slot id
    /// into the device filling it, so a link made to a slot matches a binding made to the device, and the other way round.
    /// </summary>
    public static List<(Binding Binding, string[] Parts)> BindingsFor(PanelControl control, Profile profile, Func<string, string> resolve)
    {
        bool Same(InputRef? a, InputRef b) =>
            a is not null && a.Kind == b.Kind && a.Index == b.Index &&
            (a.DeviceId == b.DeviceId || string.Equals(resolve(a.DeviceId), resolve(b.DeviceId), StringComparison.Ordinal));

        var result = new List<(Binding, string[])>();
        foreach (var b in profile.Bindings)
        {
            var parts = control.Links
                .Where(l => Same(b.Input, l.Input) || b.ExtraInputs.Any(x => Same(x, l.Input)))
                .Select(l => l.Part).Distinct().ToArray();
            if (parts.Length > 0) result.Add((b, parts));
        }
        return result;
    }

    /// <summary>Groups the control's linked parts by input (an analog stick's Up and Down are one axis).</summary>
    public static List<(InputRef Input, string[] Parts)> InputsOf(PanelControl control) =>
        control.Parts.Select(p => control.Link(p)).Where(l => l is not null)
            .GroupBy(l => l!.Input).Select(g => (g.Key, g.Select(l => l!.Part).ToArray())).ToList();

    /// <summary>A short caption for the control: the outputs of the enabled bindings, e.g. "A", "Z · X", "LeftX · LeftY".</summary>
    public static string Caption(IEnumerable<(Binding Binding, string[] Parts)> bindings, int max = 2)
    {
        var names = bindings.Where(x => x.Binding.Enabled && x.Binding.Output is not null)
            .Select(x => OutputNames.Short(x.Binding.Output!)).Distinct().ToList();
        if (names.Count <= max) return string.Join(" · ", names);
        return string.Join(" · ", names.Take(max)) + $" +{names.Count - max}";
    }
}
