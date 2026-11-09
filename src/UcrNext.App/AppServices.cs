using UcrNext.Core;
using UcrNext.Devices;

namespace UcrNext.App;

/// <summary>Wires device providers, outputs, the engine and the profile store together.</summary>
public sealed class AppServices : IDisposable
{
    public AppSettings Settings { get; }
    public string SettingsPath { get; }
    public ProfileStore Store { get; }
    public KeyboardMouseHookProvider Hooks { get; }
    public RawInputProvider RawInput { get; }
    public ViGEmPadOutput Pad { get; }
    public VJoyOutput VJoy { get; }
    public DirectInputProvider? DirectInput { get; }
    public InterceptionProvider? Interception { get; }
    public CompositeInputSource Inputs { get; }
    public MappingEngine Engine { get; }
    public EngineRunner Runner { get; }
    public ForegroundWatcher Foreground { get; }

    public event Action<Exception>? EngineError;

    /// <summary>
    /// Cached: checking it runs schtasks.exe, which must not happen on every status refresh
    /// (the UI thread has to stay responsive). Updated when the user changes it.
    /// </summary>
    public Autostart.Mode AutostartMode { get; set; } = Autostart.Current();

    /// <summary>Must be constructed on the UI thread (the keyboard/mouse hooks need its message loop).</summary>
    public AppServices(string? profileDirectory, string settingsPath)
    {
        SettingsPath = settingsPath;
        Settings = AppSettings.Load(settingsPath);
        Store = new ProfileStore(profileDirectory ?? ProfileStore.DefaultDirectory);
        Pad = new ViGEmPadOutput();
        VJoy = new VJoyOutput();

        MappingEngine? engine = null; // the hooks/Interception callbacks only run after it is set
        Hooks = new KeyboardMouseHookProvider(i => engine?.IsBlocked(i) == true, () => engine?.HasBlocks == true);

        var providers = new List<IInputProvider> { new XInputProvider(() => Pad.ExcludedSlots) };
        var di = new DirectInputProvider();
        if (di.IsAvailable) { DirectInput = di; providers.Add(di); }
        else providers.Add(new WinMmJoystickProvider()); // fallback
        if (Settings.UseInterception)
        {
            NativeLibraries.InterceptionFolder = Settings.InterceptionDllFolder;
            var icpt = new InterceptionProvider(i => engine?.IsBlocked(i) == true, () => engine?.HasBlocks == true);
            Interception = icpt;
            if (icpt.Start()) providers.Add(icpt);
        }
        RawInput = new RawInputProvider();
        providers.Add(RawInput); // before the hooks: owns mouse movement, wheel and per-device mouse buttons
        providers.Add(Hooks);

        Inputs = new CompositeInputSource(providers);
        Engine = engine = new MappingEngine(Inputs, new SendInputOutput(), Pad, VJoy);
        Hooks.Install();
        RawInput.Install();

        Foreground = new ForegroundWatcher();
        Foreground.Changed += fg => Engine.ForegroundApp = fg;
        Engine.ForegroundApp = Foreground.Current;

        Runner = new EngineRunner(Inputs, Engine);
        Runner.Error += ex => EngineError?.Invoke(ex);
        Runner.Start(); // polls always; mapping is switched on separately
    }

    public void SaveSettings() => Settings.Save(SettingsPath);

    /// <summary>One line per subsystem, for the status bar tooltip and "ucrnext status".</summary>
    public IEnumerable<string> StatusLines()
    {
        yield return "Foreground app: " + (Engine.ForegroundApp ?? "(none)");
        var skipped = Engine.SkippedProfileNames;
        if (skipped.Count > 0) yield return "Skipped (device connected): " + string.Join(", ", skipped);
        yield return "Xbox 360 pad: " + Pad.Status;
        yield return VJoy.Status;
        yield return DirectInput?.Status ?? "DirectInput unavailable (using WinMM fallback)";
        yield return Interception?.Status ?? "Interception: off (enable in Settings)";
        yield return "Devices: " + string.Join(", ", Inputs.Devices.Select(d => d.Name));
        var (physical, injected) = Hooks.KeyEvents;
        yield return $"Keyboard/mouse hooks: {(Hooks.IsInstalled ? "installed" : "NOT installed")}, " +
                     $"key events: {physical} from keyboards, {injected} from software (ignored)";
        yield return "Autostart: " + AutostartMode;
        yield return "Profiles folder: " + Store.Directory;
    }

    public void Dispose()
    {
        Foreground.Dispose();
        Runner.Dispose();
        Interception?.Dispose();
        DirectInput?.Dispose();
        Pad.Dispose();
        VJoy.Dispose();
        Hooks.Dispose();
        RawInput.Dispose();
    }
}
