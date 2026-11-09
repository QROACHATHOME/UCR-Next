using System.Text;

namespace UcrNext.Core;

/// <summary>What the command processor can ask the running app to do.</summary>
public interface IControlHost
{
    bool IsMapping { get; set; }
    /// <summary>Profiles in display order.</summary>
    IReadOnlyList<Profile> Profiles { get; }
    void SetProfileEnabled(Profile profile, bool enabled);
    void ReloadProfiles();
    /// <summary>Edits made in the window that aren't saved yet.</summary>
    bool HasUnsavedChanges { get; }
    void SaveProfiles();
    void ShowWindow();
    void HideWindow();
    void Quit();
    /// <summary>Extra status lines (foreground app, devices, drivers…).</summary>
    IEnumerable<string> StatusLines();
    IReadOnlyList<string> ActiveProfileNames { get; }
    /// <summary>Imports a UCR context.xml and returns the report text.</summary>
    string ImportUcr(string path, bool perDevice);
}

/// <summary>Parses text commands (from the CLI pipe) and applies them to an <see cref="IControlHost"/>.</summary>
public sealed class CommandProcessor
{
    public const string Help =
        "Commands:\n" +
        "  status                 mapping state, active profiles, devices\n" +
        "  start | stop | toggle  turn mapping on / off\n" +
        "  profiles               list profiles\n" +
        "  enable <profile>       enable a profile\n" +
        "  disable <profile>      disable a profile\n" +
        "  solo <profile>         enable only this profile, disable all others\n" +
        "  games                  list games and the profiles they use\n" +
        "  game <game> [--start]  enable only the profile(s) listed for this game\n" +
        "  import <context.xml> [--per-device]   import UCR profiles\n" +
        "  save                   save edits made in the window\n" +
        "  reload                 reload profiles from disk (drops unsaved edits)\n" +
        "  show | hide            show or hide the window\n" +
        "  quit [--save|--discard]  exit UCR-Next (with unsaved edits, say what to do with them)\n" +
        "Profile names are case-insensitive; a unique prefix is enough. Quote names with spaces.";

    private readonly IControlHost _host;

    public CommandProcessor(IControlHost host) => _host = host;

    public string Execute(string commandLine)
    {
        var args = CommandLine.Split(commandLine);
        if (args.Count == 0) return Help;
        string cmd = args[0].ToLowerInvariant();
        string rest = string.Join(" ", args.Skip(1));

        switch (cmd)
        {
            case "help": case "?": case "-h": case "--help":
                return Help;
            case "status":
                return Status();
            case "start":
                _host.IsMapping = true;
                return "Mapping ON";
            case "stop":
                _host.IsMapping = false;
                return "Mapping OFF";
            case "toggle":
                _host.IsMapping = !_host.IsMapping;
                return _host.IsMapping ? "Mapping ON" : "Mapping OFF";
            case "profiles":
                return ListProfiles();
            case "enable":
            case "disable":
            {
                var (p, err) = Find(rest);
                if (p is null) return err!;
                _host.SetProfileEnabled(p, cmd == "enable");
                return $"Profile \"{p.Name}\" {(cmd == "enable" ? "enabled" : "disabled")}";
            }
            case "solo":
            {
                var (p, err) = Find(rest);
                if (p is null) return err!;
                foreach (var other in _host.Profiles) _host.SetProfileEnabled(other, ReferenceEquals(other, p));
                return $"Only \"{p.Name}\" is enabled";
            }
            case "games":
                return ListGames();
            case "game":
            {
                bool start = args.Skip(1).Any(a => a.Equals("--start", StringComparison.OrdinalIgnoreCase));
                string game = string.Join(" ", args.Skip(1).Where(a => !a.Equals("--start", StringComparison.OrdinalIgnoreCase)));
                var (matches, err) = FindGame(game);
                if (matches is null) return err!;
                foreach (var p in _host.Profiles) _host.SetProfileEnabled(p, matches.Contains(p));
                if (start) _host.IsMapping = true;
                return $"Enabled for \"{game}\": {string.Join(", ", matches.Select(p => p.Name))}" + (start ? " — mapping ON" : "");
            }
            case "import":
            {
                bool perDevice = args.Skip(1).Any(a => a.Equals("--per-device", StringComparison.OrdinalIgnoreCase));
                string path = string.Join(" ", args.Skip(1).Where(a => !a.StartsWith("--")));
                if (string.IsNullOrWhiteSpace(path)) return "error: path to UCR context.xml required";
                return _host.ImportUcr(path, perDevice);
            }
            case "save":
                if (!_host.HasUnsavedChanges) return "Nothing to save";
                _host.SaveProfiles();
                return "Saved";
            case "reload":
                _host.ReloadProfiles();
                return $"Reloaded {_host.Profiles.Count} profile(s)";
            case "show":
                _host.ShowWindow();
                return "Window shown";
            case "hide":
                _host.HideWindow();
                return "Window hidden";
            case "quit": case "exit":
            {
                bool save = args.Skip(1).Any(a => a.Equals("--save", StringComparison.OrdinalIgnoreCase));
                bool discard = args.Skip(1).Any(a => a.Equals("--discard", StringComparison.OrdinalIgnoreCase));
                if (_host.HasUnsavedChanges && !save && !discard)
                    return "error: UCR-Next has unsaved changes. Save or revert them in the window, or run 'quit --save' or 'quit --discard'.";
                if (save && _host.HasUnsavedChanges) _host.SaveProfiles();
                _host.Quit();
                return "UCR-Next is exiting";
            }
            default:
                return $"error: unknown command \"{args[0]}\"\n\n{Help}";
        }
    }

    private string Status()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Mapping: " + (_host.IsMapping ? "ON" : "OFF"));
        if (_host.HasUnsavedChanges) sb.AppendLine("Unsaved changes: yes (save them in the window or with 'ucrnext save')");
        var active = _host.ActiveProfileNames;
        sb.AppendLine("Active profiles: " + (active.Count == 0 ? "(none)" : string.Join(", ", active)));
        foreach (var l in _host.StatusLines()) sb.AppendLine(l);
        return sb.ToString().TrimEnd();
    }

    private string ListProfiles()
    {
        if (_host.Profiles.Count == 0) return "(no profiles)";
        var sb = new StringBuilder();
        foreach (var p in _host.Profiles)
        {
            sb.Append(p.Enabled ? "[x] " : "[ ] ").Append(p.Name)
              .Append($"  ({p.Bindings.Count} binding{(p.Bindings.Count == 1 ? "" : "s")})");
            if (p.Applications.Count > 0) sb.Append("  apps: ").Append(string.Join(", ", p.Applications));
            if (p.SkipIfDevicesPresent.Count > 0) sb.Append("  skipped while connected: ").Append(string.Join(", ", p.SkipIfDevicesPresent));
            if (p.Games.Count > 0) sb.Append("  games: ").Append(string.Join(", ", p.Games));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private string ListGames()
    {
        var rows = _host.Profiles
            .SelectMany(p => p.Games.Select(g => (Game: g, Profile: p.Name)))
            .GroupBy(r => r.Game, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => $"{g.Key}  →  {string.Join(", ", g.Select(r => r.Profile))}")
            .ToList();
        return rows.Count == 0 ? "(no games listed on any profile)" : string.Join("\n", rows);
    }

    /// <summary>Profiles listing the game: exact name first, else a unique partial match.</summary>
    private (List<Profile>?, string?) FindGame(string game)
    {
        if (string.IsNullOrWhiteSpace(game)) return (null, "error: game name required");
        var exact = _host.Profiles.Where(p => p.Games.Any(g => string.Equals(g, game, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0) return (exact, null);
        var names = _host.Profiles.SelectMany(p => p.Games)
            .Where(g => g.Contains(game, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 1)
            return (_host.Profiles.Where(p => p.Games.Contains(names[0], StringComparer.OrdinalIgnoreCase)).ToList(), null);
        if (names.Count == 0) return (null, $"error: no profile lists the game \"{game}\"");
        return (null, $"error: \"{game}\" matches several games: " + string.Join(", ", names));
    }

    private (Profile?, string?) Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, "error: profile name required");
        var exact = _host.Profiles.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return (exact[0], null);
        var prefix = _host.Profiles.Where(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefix.Count == 1) return (prefix[0], null);
        if (exact.Count + prefix.Count == 0) return (null, $"error: no profile named \"{name}\"");
        return (null, $"error: \"{name}\" matches several profiles: " + string.Join(", ", prefix.Select(p => p.Name)));
    }
}
