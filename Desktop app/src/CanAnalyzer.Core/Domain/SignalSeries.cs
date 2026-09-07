namespace CanAnalyzer.Core.Domain;

/// <summary>Cached exact timestamps and double-precision values for one signal.</summary>
public sealed class SignalSeries
{
    private readonly object _loadLock = new();
    private Func<(long[] Timestamps, double[] Values)>? _loader;
    private readonly Func<int, (long[] Timestamps, double[] Values)>? _sampledLoader;
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
        SampleCount = value.Length;
    }

    public SignalSeries(SignalIdentity identity, long[] timestampNanoseconds, double[] value, string labelOverride)
        : this(identity, timestampNanoseconds, value)
    {
        if (string.IsNullOrWhiteSpace(labelOverride))
        {
            throw new ArgumentException("Label override cannot be empty.", nameof(labelOverride));
        }
        LabelOverride = labelOverride;
    }

    public SignalSeries(SignalIdentity identity, Func<(long[] Timestamps, double[] Values)> loader)
    {
        Identity = identity;
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    public SignalSeries(
        SignalIdentity identity,
        int sampleCount,
        Func<(long[] Timestamps, double[] Values)> loader,
        Func<int, (long[] Timestamps, double[] Values)> sampledLoader)
    {
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        Identity = identity;
        SampleCount = sampleCount;
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _sampledLoader = sampledLoader ?? throw new ArgumentNullException(nameof(sampledLoader));
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

    /// <summary>The exact number of source samples when known without loading the arrays.</summary>
    public int? SampleCount { get; }

    /// <summary>
    /// Returns this series when already bounded, otherwise a uniformly sampled copy that includes
    /// both endpoints. Disk-backed series use their index and do not load the full source arrays.
    /// </summary>
    public SignalSeries ForAnalysis(int maximumPoints)
    {
        if (maximumPoints < 2) throw new ArgumentOutOfRangeException(nameof(maximumPoints));
        if (SampleCount is { } count && count <= maximumPoints) return this;

        var sampled = _sampledLoader is not null
            ? _sampledLoader(maximumPoints)
            : SampleLoaded(TimestampNanoseconds, Value, maximumPoints);
        return new SignalSeries(Identity, sampled.Timestamps, sampled.Values, Label);
    }

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

    private static (long[] Timestamps, double[] Values) SampleLoaded(long[] timestamps, double[] values, int maximumPoints)
    {
        if (values.Length <= maximumPoints) return (timestamps, values);
        var sampledTimestamps = new long[maximumPoints];
        var sampledValues = new double[maximumPoints];
        for (var target = 0; target < maximumPoints; target++)
        {
            var source = (long)target * (values.Length - 1L) / (maximumPoints - 1L);
            sampledTimestamps[target] = timestamps[source];
            sampledValues[target] = values[source];
        }

        return (sampledTimestamps, sampledValues);
    }
}
