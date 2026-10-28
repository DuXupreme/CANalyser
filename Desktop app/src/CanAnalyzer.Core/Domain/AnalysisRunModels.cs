using CanAnalyzer.Core.Analysis;

namespace CanAnalyzer.Core.Domain;

/// <summary>Versioned, explicitly scoped interpretation. Signal labels are never persisted as keys.</summary>
public sealed record AnalysisProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "Automatisch herkende signalen";
    public string DbcSha256 { get; init; } = string.Empty;
    public string? MachineId { get; init; }
    public string? InstallationId { get; init; }
    public string? SourceLogSha256 { get; init; }
    public bool Confirmed { get; init; }
    public string Evidence { get; init; } = "Naamherkenning; fysieke grenzen zijn niet afgeleid uit loguitersten.";
    public IReadOnlyList<SignalDefinitionReference> SignalDefinitions { get; init; } = [];
    public IReadOnlyList<SignalRoleCandidates> MappingCandidates { get; init; } = [];
    /// <summary>Assigned definitions changed or disappeared; retained to avoid silently dropping optional filters.</summary>
    public IReadOnlyList<SignalIdentity> InvalidatedSignals { get; init; } = [];
    public IReadOnlyList<JoystickAnalysisProfile> Joysticks { get; init; } = [];
    public IReadOnlyList<ActuatorAnalysisProfile> Actuators { get; init; } = [];
    public UsageAnalysisProfile? Usage { get; init; }
    public BatteryAnalysisProfile? Battery { get; init; }
    public double MaximumGapSeconds { get; init; } = .5;
    public double CanBitrateKbps { get; init; } = 500;
    public double CanDataBitrateKbps { get; init; } = 2000;
    public bool CanBitratesConfirmed { get; init; }
    public IReadOnlyList<string> ConfirmedDecoderChannels { get; init; } = [];
}

public sealed record SignalDefinitionReference(SignalIdentity Identity, string DefinitionSha256);
public sealed record SignalRoleCandidates(string SubjectId, string Role, IReadOnlyList<SignalIdentity> Candidates);

public sealed record JoystickAnalysisProfile(string Id, string Name, SignalIdentity? X, SignalIdentity? Y)
{
    public JoystickAxisCalibration? XCalibration { get; init; }
    public JoystickAxisCalibration? YCalibration { get; init; }
    public bool CalibrationConfirmed { get; init; }
    public double NeutralThreshold { get; init; } = .1;
    public double FullThreshold { get; init; } = .9;
    public int HistogramBins { get; init; } = 40;
}

public sealed record ActuatorAnalysisProfile(string Id, string Name, SignalIdentity? Command, SignalIdentity? Feedback)
{
    public SignalIdentity? RunSignal { get; init; }
    public double RunValue { get; init; } = 1;
    public bool PositionReferenceConfirmed { get; init; }
    public MotionResponseOptions ResponseOptions { get; init; } = new();
    public StrokeAnalysisProfile? Stroke { get; init; }
}

/// <summary>Known physical references, never inferred from observed extrema.</summary>
public sealed record StrokeAnalysisProfile
{
    public bool CalibrationConfirmed { get; init; }
    public double? ManualCenter { get; init; }
    public double? HalfRange { get; init; }
    public SignalIdentity? CenterSignal { get; init; }
    public SignalIdentity? HalfRangeSignal { get; init; }
    public double EdgePercent { get; init; } = 5;
    public int HistogramBins { get; init; } = 40;
}

public sealed record UsageAnalysisProfile
{
    public SignalIdentity? Activity { get; init; }
    public SignalIdentity? Soc { get; init; }
    public SignalIdentity? On { get; init; }
    public ActiveUsageOptions Options { get; init; } = new();
    public bool ThresholdConfirmed { get; init; }
    public bool CapacityConfirmed { get; init; }
}

public sealed record BatteryAnalysisProfile(SignalIdentity? ActivePackCount, int? ConfirmedCount);

public sealed record AnalysisRunRequest
{
    public AnalysisProfile? Profile { get; init; }
    public string Scope { get; init; } = "FullDataset";
    public double? StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public AnalysisModules Modules { get; init; } = AnalysisModules.All;
    public bool IncludeSamples { get; init; }
}

[Flags]
public enum AnalysisModules
{
    None = 0, Joystick = 1, Actuators = 2, Can = 4, ActiveUsage = 8, Battery = 16, Quality = 32,
    All = Joystick | Actuators | Can | ActiveUsage | Battery | Quality
}

public enum ModuleStatus { Available, Partial, MissingSignals, AmbiguousMapping, NeedsConfiguration, InsufficientCoverage, Failed, NotRequested }

public sealed record AnalysisCoverage(double RequestedSeconds, double ObservedSeconds, double EligibleSeconds)
{
    public double ExcludedSeconds => Math.Max(0, RequestedSeconds - EligibleSeconds);
}

public sealed record ModuleResult<T>
{
    public string ResultId { get; init; } = string.Empty;
    public required string ModuleId { get; init; }
    public required string SubjectId { get; init; }
    public string MethodVersion { get; init; } = "1";
    public required ModuleStatus Status { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];
    public string Explanation { get; init; } = string.Empty;
    /// <summary>Primary result unit; composite summaries use mixed and specify field units below.</summary>
    public string Unit { get; init; } = "mixed";
    public IReadOnlyDictionary<string, string> ValueUnits { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<SignalIdentity> Inputs { get; init; } = [];
    public AnalysisCoverage Coverage { get; init; } = new(0, 0, 0);
    public T? Value { get; init; }
}

/// <summary>Source points are intentionally absent; the raw-sample export is a separate opt-in.</summary>
public sealed record JoystickUsageSummary(bool IsCalibrated, double CoveredSeconds, double WindowSeconds,
    IReadOnlyList<UsageBin> RadiusDistribution, IReadOnlyList<JoystickAmplitudeBand> AmplitudeBands,
    IReadOnlyList<JoystickInputEpisode> InputEpisodes, IReadOnlyList<JoystickFullHold> FullHolds,
    double OutsideCalibrationSeconds);

public sealed record ActuatorAnalysisResult(ModuleResult<MotionResponseResult> SetpointChange,
    ModuleResult<MotionResponseResult> PositionRequest, ModuleResult<MotionTrackingResult> Tracking)
{
    public ModuleResult<CalibratedStrokeResult>? Stroke { get; init; }
}

/// <summary>SOC-derived energy is only exposed when a capacity has been confirmed.</summary>
public sealed record UsageAnalysisSummary(IReadOnlyList<UsageInterval> Intervals, double ActiveSeconds,
    double IdleSeconds, double UnknownSeconds, double? ActivePercent, double SocCoveredSeconds,
    double DischargeSocPoints, double RiseSocPoints, double? ConfirmedCapacityKwh, double? NetKwh,
    double? ActiveRuntimeHours, double? RemainingActiveHours, bool HasOnSignal);

public sealed record CanStreamSummary(uint FrameId, bool Extended, CanFrameFormat Format, CanFrameDirection Direction,
    long Count, long CycleCount, double? MeanCycleMilliseconds, double? MinimumCycleMilliseconds,
    double? MaximumCycleMilliseconds, long NonIncreasingIntervals);
public sealed record CanMinuteSummary(double StartSeconds, long Frames);
public sealed record CanChannelResult(long FrameCount, double StartSeconds, double EndSeconds,
    long ErrorFrames, long RemoteFrames, double? FramesPerSecond, double? EstimatedBusLoadPercent,
    bool BitratesConfirmed, IReadOnlyList<CanStreamSummary> Streams, IReadOnlyList<CanMinuteSummary> Minutes);
public sealed record DatasetQualityResult(DatasetCompleteness Completeness, string ClockQuality,
    IReadOnlyList<string> MachineIds, long RejectedRecords, IReadOnlyList<MeasurementGap> Gaps,
    LoggerDiagnostics LoggerDiagnostics);

public sealed record StandardAnalysisRun
{
    public int SchemaVersion { get; init; } = 1;
    public string MethodVersion { get; init; } = "standard-analysis-1";
    public string Scope { get; init; } = "FullDataset";
    public AnalysisModules Modules { get; init; } = AnalysisModules.All;
    public required string DatasetFingerprint { get; init; }
    public required AnalysisProfile Profile { get; init; }
    public IReadOnlyList<string> SourceHashes { get; init; } = [];
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public IReadOnlyList<ModuleResult<JoystickUsageSummary>> Joysticks { get; init; } = [];
    public IReadOnlyList<ModuleResult<ActuatorAnalysisResult>> Actuators { get; init; } = [];
    public IReadOnlyList<ModuleResult<CanChannelResult>> CanChannels { get; init; } = [];
    public required ModuleResult<UsageAnalysisSummary> ActiveUsage { get; init; }
    public required ModuleResult<BatteryConfigurationResult> BatteryConfiguration { get; init; }
    public required ModuleResult<DatasetQualityResult> Quality { get; init; }
}
