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
    void ShowWindow();
    void HideWindow();
    void Quit();
    /// <summary>Extra status lines (foreground app, devices, drivers…).</summary>
    IEnumerable<string> StatusLines();
    IReadOnlyList<string> ActiveProfileNames { get; }
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
        "  reload                 reload profiles from disk\n" +
        "  show | hide            show or hide the window\n" +
        "  quit                   exit UCR-Next\n" +
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
                _host.Quit();
                return "UCR-Next is exiting";
            default:
                return $"error: unknown command \"{args[0]}\"\n\n{Help}";
        }
    }

    private string Status()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Mapping: " + (_host.IsMapping ? "ON" : "OFF"));
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
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
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
