namespace CanAnalyzer.Core.Domain;

/// <summary>Cached exact timestamps and double-precision values for one signal.</summary>
public sealed class SignalSeries
{
    private readonly object _loadLock = new();
    private Func<(long[] Timestamps, double[] Values)>? _loader;
    private long[]? _timestampNanoseconds;
    private double[]? _value;
    private double[]? _timeSeconds;

    public SignalSeries(SignalIdentity identity, long[] timestampNanoseconds, double[] value)
    {
        if (timestampNanoseconds.Length != value.Length)
        {
            throw new ArgumentException("Time and value arrays must have the same length.");
        }

        Identity = identity;
        _timestampNanoseconds = timestampNanoseconds;
        _value = value;
    }

    public SignalSeries(SignalIdentity identity, Func<(long[] Timestamps, double[] Values)> loader)
    {
        Identity = identity;
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    public SignalSeries(string label, double[] timeSeconds, double[] value)
        : this(
            new SignalIdentity(string.Empty, CanFrameFormat.Classic, false, 0, label, string.Empty),
            timeSeconds.Select(ToNanoseconds).ToArray(),
            value)
    {
        LabelOverride = label;
        _timeSeconds = timeSeconds;
    }

    public SignalIdentity Identity { get; }
    public long[] TimestampNanoseconds
    {
        get { EnsureLoaded(); return _timestampNanoseconds!; }
    }

    public double[] Value
    {
        get { EnsureLoaded(); return _value!; }
    }
    public string Label => LabelOverride ?? Identity.DisplayLabel;
    private string? LabelOverride { get; }

    /// <summary>Read-only projection for plotting/numerical algorithms.</summary>
    public double[] Time => _timeSeconds ??= TimestampNanoseconds.Select(static value => value / 1_000_000_000d).ToArray();

    public bool IsMaterialized => _loader is null;

    private void EnsureLoaded()
    {
        if (_loader is null) return;
        lock (_loadLock)
        {
            if (_loader is null) return;
            var loaded = _loader();
            if (loaded.Timestamps.Length != loaded.Values.Length)
                throw new InvalidDataException("Lazy signal series returned unequal timestamp and value counts.");
            _timestampNanoseconds = loaded.Timestamps;
            _value = loaded.Values;
            _loader = null;
        }
    }

    private static long ToNanoseconds(double seconds) =>
        checked((long)Math.Round(seconds * 1_000_000_000d, MidpointRounding.AwayFromZero));
}
