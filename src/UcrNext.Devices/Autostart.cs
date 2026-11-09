using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace UcrNext.Devices;

/// <summary>
/// Start UCR-Next silently when the user logs on. Two modes:
/// <list type="bullet">
/// <item>Logon: HKCU Run key, no admin rights needed.</item>
/// <item>Elevated: a Task Scheduler logon task with highest privileges, so UCR-Next can also send
/// input to games running as administrator. Creating/removing it shows one UAC prompt.</item>
/// </list>
/// A real Windows service is deliberately not used: services run in session 0, where they cannot
/// see the user's keyboard/mouse or send input to the user's desktop.
/// </summary>
public static class Autostart
{
    public const string Name = "UCR-Next";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string LaunchArgs = "--background --start";

    public enum Mode { Off, Logon, Elevated }

    public static Mode Current()
    {
        if (TaskExists()) return Mode.Elevated;
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string ? Mode.Logon : Mode.Off;
    }

    /// <summary>Path of UCR-Next.exe (works when called from the app or from the CLI next to it).</summary>
    public static string AppExe()
    {
        string here = Path.Combine(AppContext.BaseDirectory, "UCR-Next.exe");
        return File.Exists(here) ? here : Environment.ProcessPath!;
    }

    /// <summary>Applies the mode. Returns an error message, or null on success.</summary>
    public static string? Set(Mode mode, string? exe = null)
    {
        exe ??= AppExe();
        try
        {
            // Remove whatever is there first so only one autostart entry exists.
            if (mode != Mode.Elevated && TaskExists())
            {
                var err = RunSchtasksElevated($"/Delete /TN \"{Name}\" /F");
                if (err is not null) return err;
            }
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (mode == Mode.Logon) k.SetValue(Name, $"\"{exe}\" {LaunchArgs}");
                else k.DeleteValue(Name, false);
            }
            if (mode == Mode.Elevated)
            {
                string xml = Path.Combine(Path.GetTempPath(), "ucrnext-task.xml");
                File.WriteAllText(xml, TaskXml(exe), System.Text.Encoding.Unicode);
                var err = RunSchtasksElevated($"/Create /TN \"{Name}\" /XML \"{xml}\" /F");
                try { File.Delete(xml); } catch { }
                if (err is not null) return err;
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool TaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{Name}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? RunSchtasksElevated(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit(30000);
            return p.ExitCode == 0 ? null : $"schtasks failed (exit code {p.ExitCode})";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "cancelled at the administrator prompt";
        }
    }

    /// <summary>
    /// Task definition: runs at this user's logon, interactive (same desktop as the user),
    /// highest privileges, no time limit, runs on battery.
    /// </summary>
    private static string TaskXml(string exe)
    {
        string user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        string cmd = SecurityElement.Escape(exe);
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exe)!);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts UCR-Next (input remapper) in the background at logon.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{cmd}</Command>
                  <Arguments>{LaunchArgs}</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
