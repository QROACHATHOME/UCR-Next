using System.Diagnostics;
using UcrNext.Core;
using UcrNext.Devices;

// ucrnext.exe — command-line control for a running UCR-Next.
// Exit codes: 0 ok, 1 error / not running, 2 usage.

const string Usage =
    "ucrnext <command> [args]\n\n" +
    CommandProcessor.Help + "\n\n" +
    "Local commands (work without a running instance):\n" +
    "  launch [--background] [--start]    start UCR-Next if it is not running\n" +
    "  autostart [status|off|logon|elevated]\n" +
    "        logon    = start hidden at Windows logon\n" +
    "        elevated = same, as administrator (via Task Scheduler; shows a UAC prompt)\n";

if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}

string cmd = args[0].ToLowerInvariant();

if (cmd == "launch")
{
    if (CommandPipe.Send("status", 300) is not null)
    {
        Console.WriteLine("UCR-Next is already running.");
        return 0;
    }
    string exe = Autostart.AppExe();
    if (!File.Exists(exe) || !exe.EndsWith("UCR-Next.exe", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("UCR-Next.exe was not found next to ucrnext.exe.");
        return 1;
    }
    Process.Start(new ProcessStartInfo(exe, string.Join(" ", args.Skip(1))) { UseShellExecute = true });
    for (int i = 0; i < 50; i++)
    {
        Thread.Sleep(200);
        if (CommandPipe.Send("status", 300) is not null) { Console.WriteLine("UCR-Next started."); return 0; }
    }
    Console.Error.WriteLine("UCR-Next was launched but is not answering yet.");
    return 1;
}

if (cmd == "autostart")
{
    string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
    if (sub == "status")
    {
        Console.WriteLine("Autostart: " + Describe(Autostart.Current()));
        return 0;
    }
    Autostart.Mode? mode = sub switch
    {
        "off" => Autostart.Mode.Off,
        "logon" or "on" => Autostart.Mode.Logon,
        "elevated" or "admin" => Autostart.Mode.Elevated,
        _ => null,
    };
    if (mode is null) { Console.Error.WriteLine(Usage); return 2; }
    var err = Autostart.Set(mode.Value);
    if (err is not null) { Console.Error.WriteLine("error: " + err); return 1; }
    Console.WriteLine("Autostart: " + Describe(Autostart.Current()));
    return 0;
}

var reply = CommandPipe.Send(string.Join(" ", args.Select(CommandLine.Quote)));
if (reply is null)
{
    Console.Error.WriteLine("UCR-Next is not running. Start it with: ucrnext launch --background");
    return 1;
}
Console.WriteLine(reply);
return reply.StartsWith("error:", StringComparison.Ordinal) ? 1 : 0;

static string Describe(Autostart.Mode m) => m switch
{
    Autostart.Mode.Logon => "on — starts hidden at logon (Run key)",
    Autostart.Mode.Elevated => "on — starts hidden at logon as administrator (Task Scheduler)",
    _ => "off",
};
