namespace UcrNext.Core;

/// <summary>Routes reads to whichever provider owns the device.</summary>
public sealed class CompositeInputSource : IInputSource, IDeltaSource
{
    private readonly IReadOnlyList<IInputProvider> _providers;

    public CompositeInputSource(IReadOnlyList<IInputProvider> providers) => _providers = providers;

    public IReadOnlyList<IInputProvider> Providers => _providers;

    public bool TryRead(InputRef input, out double value)
    {
        foreach (var p in _providers)
            if (p.TryRead(input, out value)) return true;
        value = 0;
        return false;
    }

    public bool TryReadDelta(InputRef input, out double sum, out int events)
    {
        foreach (var p in _providers)
            if (p is IDeltaSource d && d.TryReadDelta(input, out sum, out events)) return true;
        sum = 0; events = 0;
        return false;
    }

    public void PollAll()
    {
        foreach (var p in _providers) p.Poll();
    }

    public IEnumerable<(InputRef Input, double Value)> ReadAll() => _providers.SelectMany(p => p.ReadAll());

    public IEnumerable<DeviceInfo> Devices => _providers.SelectMany(p => p.Devices);
}
