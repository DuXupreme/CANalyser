namespace CanAnalyzer.Core.Domain;

/// <summary>Exact frame-stream identity; IDs on different formats or directions are never combined.</summary>
public sealed record CanDetailStreamKey(string Channel, CanFrameDirection Direction, CanFrameFormat FrameFormat,
    bool IsExtended, uint FrameId);

public sealed record CanDetailStream(CanDetailStreamKey Key, long Count, double MeanPayloadLength,
    long CycleSamples, double? AverageCycleMs, double? JitterMs, double? MinCycleMs, double? MaxCycleMs,
    long NegativeCycleCount, long NonIncreasingCycleCount, long ExcludedGapCycles);

/// <summary>Empty coverage is unknown, not zero traffic. Rates use only observed duration in this bin.</summary>
public sealed record CanDetailBin(double StartSeconds, double EndSeconds, double CoveredSeconds, long FrameCount,
    double? EstimatedTransmissionSeconds, double? FramesPerSecond, double? BusLoadPercent);

/// <summary>Numerical CAN detail, independent of UI plots and formatting. No raw frames are copied into the result.</summary>
public sealed record CanDetailResult(string Channel, double StartSeconds, double EndSeconds, double DurationSeconds,
    long FrameCount, long ExtendedCount, long ErrorFrames, long RemoteFrames, int UniqueIds,
    double? EstimatedTransmissionSeconds, double? AverageFramesPerSecond, double? PeakFramesPerSecond,
    double? AverageBusLoadPercent, double? PeakBusLoadPercent, bool BitratesConfirmed,
    double ArbitrationBitrateKbps, double DataBitrateKbps, double ActualBinWidthSeconds,
    IReadOnlyList<CanDetailStream> Streams, IReadOnlyList<CanDetailBin> Bins);
