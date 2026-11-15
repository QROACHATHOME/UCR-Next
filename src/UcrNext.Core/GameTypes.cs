namespace UcrNext.Core;

/// <summary>Suggested game types for <see cref="Profile.GameType"/> (users may type their own).</summary>
public static class GameTypes
{
    public static readonly IReadOnlyList<string> Suggested = new[]
    {
        "Action", "Arcade", "Fighting", "Flight", "Light gun", "Pinball", "Platformer",
        "Puzzle", "Racing", "Shooter", "Sports", "Trackball / spinner",
    };

    /// <summary>Heading for profiles without a game type when the list is grouped.</summary>
    public const string None = "No game type";

    /// <summary>Trimmed; a suggested type is matched case-insensitively and returned in its usual spelling.</summary>
    public static string Normalize(string? type)
    {
        var t = (type ?? "").Trim();
        return Suggested.FirstOrDefault(s => s.Equals(t, StringComparison.OrdinalIgnoreCase)) ?? t;
    }

    /// <summary>The group a profile is shown under.</summary>
    public static string GroupOf(Profile p) => p.GameType.Trim().Length == 0 ? None : Normalize(p.GameType);

    /// <summary>Group order: alphabetical, "No game type" last.</summary>
    public static int Compare(string a, string b)
    {
        if (a == b) return 0;
        if (a == None) return 1;
        if (b == None) return -1;
        return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
    }
}
