using System.Reflection;
using System.Runtime.InteropServices;

namespace UcrNext.Devices;

/// <summary>
/// Finds the third-party native DLLs we P/Invoke but never bundle:
/// vJoyInterface.dll (installed with vJoy) and interception.dll (from the Interception release).
/// </summary>
public static class NativeLibraries
{
    public const string VJoy = "vJoyInterface.dll";
    public const string Interception = "interception.dll";

    private static int _registered;

    /// <summary>Extra folder searched for interception.dll (from settings).</summary>
    public static string? InterceptionFolder { get; set; }

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    public static IEnumerable<string> Candidates(string dll)
    {
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        yield return Path.Combine(AppContext.BaseDirectory, dll);
        if (dll.Equals(VJoy, StringComparison.OrdinalIgnoreCase))
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", arch, dll);
        if (dll.Equals(Interception, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(InterceptionFolder))
        {
            yield return Path.Combine(InterceptionFolder, dll);
            yield return Path.Combine(InterceptionFolder, arch, dll);
        }
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (!name.Equals(VJoy, StringComparison.OrdinalIgnoreCase) &&
            !name.Equals(Interception, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        foreach (var c in Candidates(name))
            if (File.Exists(c) && NativeLibrary.TryLoad(c, out var h)) return h;
        return IntPtr.Zero;
    }
}
