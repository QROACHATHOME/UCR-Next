namespace UcrNext.Core;

/// <summary>
/// The command line of the original UCR, so UCR-Next can stand in for UCR.exe:
/// <c>UCR.exe --profile "Parent,Child"</c> (also <c>-p</c>, <c>/p</c>, <c>--profile=…</c>, <c>-p:…</c>).
/// UCR splits the value on commas: the first name is matched against profiles at any level
/// (shallowest first), each further name against the children of the previous match, ignoring case.
/// If the chain breaks off, UCR uses the deepest profile it did match. Profiles imported from UCR
/// are called "Parent › Child", so the same names find the same profiles here.
/// </summary>
public static class UcrCompat
{
    public const string ChildSeparator = " › ";

    /// <summary>UCR's context.xml in the program folder (UCR-Next installed in place of UCR), or null.</summary>
    public static string? ContextNextToExe
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "context.xml");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>The value of UCR's profile option, or null if the arguments don't contain one.</summary>
    public static string? ProfileArgument(IReadOnlyList<string> args)
    {
        string? found = null;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string body = a.StartsWith("--") ? a[2..] : a.StartsWith('-') || a.StartsWith('/') ? a[1..] : "";
            if (body.Length == 0) continue;
            int sep = body.IndexOfAny(new[] { '=', ':' });
            string name = sep >= 0 ? body[..sep] : body;
            if (!name.Equals("p", StringComparison.OrdinalIgnoreCase) && !name.Equals("profile", StringComparison.OrdinalIgnoreCase)) continue;
            if (sep >= 0) found = body[(sep + 1)..];
            else if (i + 1 < args.Count) found = args[++i];
        }
        return found?.Trim().Trim('"');
    }

    /// <summary>Finds the profile UCR would load for <paramref name="search"/> ("Parent,Child"), or null.</summary>
    public static Profile? FindProfile(IEnumerable<Profile> profiles, string search)
    {
        var terms = search.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (terms.Count == 0) return null;
        var list = profiles.Select(p => (Profile: p, Path: p.Name.Split(ChildSeparator).Select(s => s.Trim()).ToArray())).ToList();

        // Longest chain first: full match, else the deepest partial match (like UCR).
        for (int n = terms.Count; n >= 1; n--)
        {
            var chain = terms.Take(n).ToArray();
            var match = list
                .Where(x => x.Path.Length >= chain.Length &&
                            x.Path.Skip(x.Path.Length - chain.Length).Zip(chain, (a, b) => a.Equals(b, StringComparison.OrdinalIgnoreCase)).All(e => e))
                .OrderBy(x => x.Path.Length)                // shallowest first, as UCR's breadth-first search
                .Select(x => x.Profile).FirstOrDefault();
            if (match is not null) return match;
        }
        // A whole name typed as-is (e.g. a UCR-Next profile with a comma in its name).
        return profiles.FirstOrDefault(p => p.Name.Equals(search.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
