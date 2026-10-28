namespace CanAnalyzer.Core.Domain;

/// <summary>Settings for source-sample command-to-meaningful-movement measurements.</summary>
public sealed record MotionResponseOptions
{
    public MotionResponseReference Reference { get; init; } = MotionResponseReference.SetpointChange;
    public double MaxDelaySeconds { get; init; } = 1.5;
    public double? ResponseThresholdAbsolute { get; init; }
    public double? PositionToleranceAbsolute { get; init; }
    public double MinimumQuietSeconds { get; init; } = 0.10;
    public double SustainedSeconds { get; init; } = 0.02;
}

/// <summary>
/// SetpointChange describes changes in the command waveform. PositionRequest requires matched
/// position units/origin and starts only when a command update creates a new directional request.
/// Neither reference is an independently observed electrical motor command.
/// </summary>
public enum MotionResponseReference { SetpointChange, PositionRequest }

public enum MotionResponseKind { Start, Reversal, Correction }

public enum MotionResponseOutcome
{
    Accepted,
    AlreadyMoving,
    NoPrehistory,
    NoResponseWithinWindow,
    InterruptedByCommand,
    MeasurementGap,
    InsufficientObservation,
    NoNewDirectionRequest
}

/// <summary>
/// An auditable measurement on the original input samples. Delay is the first sustained
/// threshold-crossing sample, not an interpolated physical movement onset.
/// </summary>
public sealed record MotionResponseEvent(
    int CommandSourceIndex,
    double CommandTimeSeconds,
    double CommandPreviousTimeSeconds,
    int Direction,
    MotionResponseKind Kind,
    MotionResponseOutcome Outcome,
    int? ReactionSourceIndex,
    double? ReactionTimeSeconds,
    double? DelaySeconds,
    double? DelayLowerBoundSeconds,
    double? DelayUpperBoundSeconds,
    double ResponseSearchEndSeconds,
    double ResponseThreshold,
    double? ResponseBaseline,
    double? ResponseTarget)
{
    public double? OriginCommandTimeSeconds { get; init; }
    public int? OriginCommandSourceIndex { get; init; }
}

public sealed record MotionResponseResult(
    string CommandSignalLabel,
    string ResponseSignalLabel,
    IReadOnlyList<MotionResponseEvent> Events,
    DelayEventStatistics StartStatistics,
    DelayEventStatistics CorrectionStatistics,
    DelayEventStatistics ReversalStatistics,
    DelayEventStatistics AllStatistics,
    double CommandThreshold,
    double ResponseThreshold,
    bool AutoThreshold,
    double CommandSampleIntervalSeconds,
    double ResponseSampleIntervalSeconds,
    double SamplingResolutionSeconds)
{
    public MotionResponseReference Reference { get; init; }
    public double? PositionTolerance { get; init; }
}
