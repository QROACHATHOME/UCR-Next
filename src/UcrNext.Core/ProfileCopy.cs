using System.Text.Json;
using System.Text.Json.Nodes;

namespace UcrNext.Core;

/// <summary>
/// Copy / paste / duplicate of profiles and bindings. The clipboard format is plain JSON text,
/// so a profile can also be shared by pasting it into a message or saved as a file.
/// </summary>
public static class ProfileCopy
{
    public const string ProfileFormat = "UCR-Next profile";
    public const string BindingsFormat = "UCR-Next bindings";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private sealed class Envelope
    {
        public string Format { get; set; } = "";
        public Profile? Profile { get; set; }
        public List<Binding>? Bindings { get; set; }
        public List<BindingGroup>? Groups { get; set; }
    }

    public static string ProfileToText(Profile p) =>
        JsonSerializer.Serialize(new Envelope { Format = ProfileFormat, Profile = p }, Options);

    /// <summary>The bindings plus the groups they are in, so pasting keeps the grouping.</summary>
    public static string BindingsToText(IEnumerable<Binding> bindings, IEnumerable<BindingGroup> profileGroups)
    {
        var list = bindings.ToList();
        var used = list.Where(b => b.GroupId is not null).Select(b => b.GroupId!.Value).ToHashSet();
        return JsonSerializer.Serialize(new Envelope
        {
            Format = BindingsFormat, Bindings = list, Groups = profileGroups.Where(g => used.Contains(g.Id)).ToList(),
        }, Options);
    }

    /// <summary>
    /// Reads clipboard/file text. Returns a profile, or bindings (+ their groups), or false if the text
    /// isn't UCR-Next data. A bare profile JSON file (as stored in the profiles folder) is accepted too.
    /// </summary>
    public static bool TryParse(string? text, out Profile? profile, out List<Binding>? bindings, out List<BindingGroup>? groups)
    {
        profile = null; bindings = null; groups = null;
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{')) return false;
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(text, Options);
            if (env?.Format == ProfileFormat && env.Profile is not null) { profile = env.Profile; return true; }
            if (env?.Format == BindingsFormat && env.Bindings is not null)
            {
                bindings = env.Bindings; groups = env.Groups ?? new();
                return true;
            }
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is not null && node.ContainsKey("Bindings") && node.ContainsKey("Name"))
            {
                profile = JsonSerializer.Deserialize<Profile>(text, Options);
                return profile is not null;
            }
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>Exact deep copy (same ids).</summary>
    public static Profile DeepCopy(Profile p) =>
        JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(p, Options), Options)!;

    /// <summary>Deep copy with new ids for the profile, its bindings and groups (group links kept).</summary>
    public static Profile CloneAsNew(Profile p, string name)
    {
        var c = DeepCopy(p);
        c.Id = Guid.NewGuid();
        c.Name = name;
        var map = new Dictionary<Guid, Guid>();
        foreach (var g in c.Groups) { var id = Guid.NewGuid(); map[g.Id] = id; g.Id = id; }
        foreach (var b in c.Bindings)
        {
            b.Id = Guid.NewGuid();
            b.GroupId = b.GroupId is { } gid && map.TryGetValue(gid, out var n) ? n : null;
        }
        return c;
    }

    /// <summary>
    /// Adds copies of <paramref name="bindings"/> to <paramref name="target"/> (new ids). A pasted
    /// binding goes into the target's group of the same name, which is created if missing;
    /// <paramref name="intoGroup"/> overrides that and puts all of them in one existing group.
    /// Returns the added bindings.
    /// </summary>
    public static List<Binding> PasteBindings(Profile target, IEnumerable<Binding> bindings, IEnumerable<BindingGroup> groups, Guid? intoGroup = null)
    {
        var srcGroups = groups.ToDictionary(g => g.Id);
        var added = new List<Binding>();
        foreach (var src in bindings)
        {
            var b = JsonSerializer.Deserialize<Binding>(JsonSerializer.Serialize(src, Options), Options)!;
            b.Id = Guid.NewGuid();
            if (intoGroup is not null) b.GroupId = intoGroup;
            else if (b.GroupId is { } gid && srcGroups.TryGetValue(gid, out var sg))
            {
                var g = target.Groups.FirstOrDefault(x => x.Id == sg.Id)
                     ?? target.Groups.FirstOrDefault(x => string.Equals(x.Name, sg.Name, StringComparison.CurrentCultureIgnoreCase));
                if (g is null)
                {
                    g = new BindingGroup { Name = sg.Name, Color = sg.Color };
                    target.Groups.Add(g);
                }
                b.GroupId = g.Id;
            }
            else b.GroupId = target.Groups.Any(g => g.Id == b.GroupId) ? b.GroupId : null;
            target.Bindings.Add(b);
            added.Add(b);
        }
        return added;
    }

    /// <summary>"Racing" -> "Racing (copy)", "Racing (copy 2)", … avoiding names in use.</summary>
    public static string CopyName(string name, IEnumerable<string> existing)
    {
        var used = existing.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name, @" \(copy( \d+)?\)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var candidate = $"{name} (copy)";
        for (int i = 2; used.Contains(candidate); i++) candidate = $"{name} (copy {i})";
        return candidate;
    }

    /// <summary>
    /// The profile's content for "has it changed since saving?": everything except the enabled
    /// checkbox and folded groups, which are saved on their own and aren't edits to undo.
    /// </summary>
    public static string EditKey(Profile p)
    {
        var node = JsonSerializer.SerializeToNode(p, Options)!.AsObject();
        node.Remove(nameof(Profile.Enabled));
        if (node[nameof(Profile.Groups)] is JsonArray groups)
            foreach (var g in groups.OfType<JsonObject>()) g.Remove(nameof(BindingGroup.Collapsed));
        return node.ToJsonString();
    }

    public static string Serialize(Profile p) => JsonSerializer.Serialize(p, Options);
    public static Profile Deserialize(string json) => JsonSerializer.Deserialize<Profile>(json, Options)!;
    public static string SerializeAll(IEnumerable<Profile> ps) => JsonSerializer.Serialize(ps.ToList(), Options);
    public static List<Profile> DeserializeAll(string json) => JsonSerializer.Deserialize<List<Profile>>(json, Options)!;
}
