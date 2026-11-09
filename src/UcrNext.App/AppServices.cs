using UcrNext.Core;
using UcrNext.Devices;

namespace UcrNext.App;

/// <summary>Wires device providers, outputs, the engine and the profile store together.</summary>
public sealed class AppServices : IDisposable
{
    public ProfileStore Store { get; }
    public KeyboardMouseHookProvider Hooks { get; }
    public ViGEmPadOutput Pad { get; }
    public CompositeInputSource Inputs { get; }
    public MappingEngine Engine { get; }
    public EngineRunner Runner { get; }

    public event Action<Exception>? EngineError;

    public AppServices(string? profileDirectory = null)
    {
        Store = new ProfileStore(profileDirectory ?? ProfileStore.DefaultDirectory);
        Pad = new ViGEmPadOutput();
        Hooks = new KeyboardMouseHookProvider();
        Hooks.Install(); // must run on the UI thread (needs its message loop)

        Inputs = new CompositeInputSource(new IInputProvider[]
        {
            new XInputProvider(() => Pad.UserIndex),
            new WinMmJoystickProvider(),
            Hooks,
        });
        Engine = new MappingEngine(Inputs, new SendInputOutput(), Pad);
        Runner = new EngineRunner(Inputs, Engine);
        Runner.Error += ex => EngineError?.Invoke(ex);
        Runner.Start(); // polls always; mapping is switched on separately
    }

    public void Dispose()
    {
        Runner.Dispose();
        Pad.Dispose();
        Hooks.Dispose();
    }
}
