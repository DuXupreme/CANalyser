namespace CanAnalyzer.Core.Domain;

/// <summary>Cached exact timestamps and double-precision values for one signal.</summary>
public sealed class SignalSeries
{
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Func<CancellationToken, (long[] Timestamps, double[] Values)>? _loader;
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
        : this(identity, WrapLoader(loader)) { }

    public SignalSeries(SignalIdentity identity, Func<CancellationToken, (long[] Timestamps, double[] Values)> loader)
    {
        Identity = identity;
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    public SignalSeries(
        SignalIdentity identity,
        int sampleCount,
        Func<(long[] Timestamps, double[] Values)> loader,
        Func<int, (long[] Timestamps, double[] Values)> sampledLoader)
        : this(identity, sampleCount, WrapLoader(loader), sampledLoader) { }

    public SignalSeries(
        SignalIdentity identity,
        int sampleCount,
        Func<CancellationToken, (long[] Timestamps, double[] Values)> loader,
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
        get => GetTimestampNanoseconds();
    }

    public double[] Value
    {
        get => GetValues();
    }
    public string Label => LabelOverride ?? Identity.DisplayLabel;
    private string? LabelOverride { get; }

    /// <summary>Read-only projection for plotting/numerical algorithms.</summary>
    public double[] Time => GetTime();

    public bool IsMaterialized => Volatile.Read(ref _loader) is null;

    public long[] GetTimestampNanoseconds(CancellationToken cancellationToken = default)
    {
        EnsureLoaded(cancellationToken);
        return _timestampNanoseconds!;
    }

    public double[] GetValues(CancellationToken cancellationToken = default)
    {
        EnsureLoaded(cancellationToken);
        return _value!;
    }

    public double[] GetTime(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _timeSeconds) is { } existing) return existing;
        EnsureLoaded(cancellationToken);
        _loadGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_timeSeconds is not null) return _timeSeconds;
            var timestamps = _timestampNanoseconds!;
            var time = new double[timestamps.Length];
            for (var i = 0; i < time.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                time[i] = timestamps[i] / 1_000_000_000d;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _timeSeconds, time);
            return time;
        }
        finally { _loadGate.Release(); }
    }

    public (double[] Time, double[] Value) GetData(CancellationToken cancellationToken = default)
        => (GetTime(cancellationToken), GetValues(cancellationToken));

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

    private static Func<CancellationToken, (long[] Timestamps, double[] Values)> WrapLoader(
        Func<(long[] Timestamps, double[] Values)> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        // Compatibility callbacks cannot be interrupted internally; production loaders accept a token.
        return _ => loader();
    }

    private void EnsureLoaded(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _loader) is null) return;
        _loadGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_loader is null) return;
            var loaded = _loader(cancellationToken);
            if (loaded.Timestamps.Length != loaded.Values.Length)
                throw new InvalidDataException("Lazy signal series returned unequal timestamp and value counts.");
            cancellationToken.ThrowIfCancellationRequested();
            _timestampNanoseconds = loaded.Timestamps;
            _value = loaded.Values;
            Volatile.Write(ref _loader, null);
        }
        finally { _loadGate.Release(); }
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
