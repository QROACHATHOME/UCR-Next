using System.IO.Pipes;
using System.Text;

namespace UcrNext.Core;

/// <summary>
/// Tiny request/response protocol over a per-user named pipe: the client writes one line
/// (the command), the server answers with text and closes. Used by the CLI and by a second
/// launch of the app to talk to the running instance.
/// </summary>
public static class CommandPipe
{
    public static string PipeName => "UcrNext.Control." + Environment.UserName;

    /// <summary>Sends a command; returns null if no instance is listening.</summary>
    public static string? Send(string command, int timeoutMs = 2000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(command.Replace('\n', ' ') + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Listens on <see cref="CommandPipe.PipeName"/> and answers each command with <c>handler</c>.</summary>
public sealed class CommandPipeServer : IDisposable
{
    private readonly Func<string, string> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public CommandPipeServer(Func<string, string> handler)
    {
        _handler = handler;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(CommandPipe.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token);

                var line = await ReadLineAsync(pipe, _cts.Token);
                string reply;
                try { reply = _handler(line.Trim()); }
                catch (Exception ex) { reply = "error: " + ex.Message; }

                var bytes = Encoding.UTF8.GetBytes(reply);
                await pipe.WriteAsync(bytes, _cts.Token);
                await pipe.FlushAsync(_cts.Token);
                pipe.WaitForPipeDrain();
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { /* client went away; keep serving */ }
        }
    }

    private static async Task<string> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < 4096 && await s.ReadAsync(one, ct) == 1 && one[0] != (byte)'\n') buf.Add(one[0]);
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(1000); } catch { /* shutting down */ }
    }
}

/// <summary>Splits "enable \"My Profile\"" into ["enable", "My Profile"].</summary>
public static class CommandLine
{
    public static List<string> Split(string text)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) { parts.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c); any = true;
        }
        if (any) parts.Add(cur.ToString());
        return parts;
    }

    public static string Quote(string s) => s.Contains(' ') || s.Length == 0 ? "\"" + s + "\"" : s;
}
