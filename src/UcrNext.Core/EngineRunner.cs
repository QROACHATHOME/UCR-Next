using System.Diagnostics;

namespace UcrNext.Core;

/// <summary>Runs poll + engine tick on a dedicated background thread at a fixed rate.</summary>
public sealed class EngineRunner : IDisposable
{
    private readonly CompositeInputSource _source;
    private readonly MappingEngine _engine;
    private readonly int _periodMs;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _mapping;

    /// <summary>Raised on the engine thread when a tick throws (the loop keeps running).</summary>
    public event Action<Exception>? Error;

    public EngineRunner(CompositeInputSource source, MappingEngine engine, int periodMs = 4)
    {
        _source = source; _engine = engine; _periodMs = periodMs;
    }

    /// <summary>True while bindings are being applied. Polling runs regardless (press-to-bind needs it).</summary>
    public bool IsMapping
    {
        get => _mapping;
        set
        {
            if (_mapping == value) return;
            _mapping = value;
            _engine.SetActive(value);
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "UcrNext engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long next = 0, nextDevices = 0;
        while (_running)
        {
            try
            {
                _source.PollAll();
                if (sw.ElapsedMilliseconds >= nextDevices)
                {
                    _engine.SetConnectedDevices(_source.Devices);
                    nextDevices = sw.ElapsedMilliseconds + 500;
                }
                if (_mapping) _engine.Tick();
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
            next += _periodMs;
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
            else if (wait < -100) next = sw.ElapsedMilliseconds; // fell far behind; resync
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        _engine.ReleaseAll();
    }
}
