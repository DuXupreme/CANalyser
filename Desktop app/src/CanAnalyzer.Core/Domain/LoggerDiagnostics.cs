namespace CanAnalyzer.Core.Domain;

public sealed record LoggerClockObservation(long TimestampNanoseconds, string Kind,
    DateTimeOffset InternalTimeUtc, DateTimeOffset? ExternalTimeUtc = null);

public sealed record LoggerStorageObservation(long TimestampNanoseconds, DateTimeOffset LoggerTimeUtc,
    int FreeSpaceMegabytes, bool Can1ReceiveEnabled, bool Can2ReceiveEnabled);

/// <summary>Recorded evidence, not a live device status and not proof of clock accuracy.</summary>
public sealed record LoggerDiagnostics
{
    public long InternalFrameCount { get; init; }
    public IReadOnlyList<LoggerClockObservation> ClockObservations { get; init; } = [];
    public LoggerStorageObservation? LastStorageObservation { get; init; }
    public bool ClockObservationsTruncated { get; init; }
    public long InvalidInternalMessageCount { get; init; }
}
