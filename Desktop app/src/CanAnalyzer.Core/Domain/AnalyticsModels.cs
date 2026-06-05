namespace CanAnalyzer.Core.Domain;

/// <summary>
/// One histogram bin.
/// </summary>
public sealed record HistogramBin(double Start, double End, int Count);

/// <summary>
/// Generic descriptive statistics for one signal.
/// </summary>
public sealed record SignalStatistics(
    int SampleCount,
    double DurationSeconds,
    double Minimum,
    double Maximum,
    double Mean,
    double Median,
    double StandardDeviation,
    double Percentile01,
    double Percentile99,
    double StartValue,
    double EndValue,
    double Delta);

/// <summary>
/// Event/change focused metrics for one signal.
/// </summary>
public sealed record SignalEventStatistics(
    double Threshold,
    double ChangeThreshold,
    int RisingCrossings,
    int FallingCrossings,
    int ChangeEvents,
    double TimeAboveThresholdPercent,
    double LongestAboveThresholdSeconds,
    double MeanAbsoluteDelta,
    double Percentile95AbsoluteDelta,
    double MaximumAbsoluteDelta,
    double MeanAbsoluteSlope,
    double MaximumAbsoluteSlope,
    double FlatlinePercent);

/// <summary>
/// Analytics result for a single selected signal.
/// </summary>
public sealed record SignalAnalyticsResult(
    string SignalLabel,
    SignalStatistics Statistics,
    SignalEventStatistics EventStatistics,
    IReadOnlyList<HistogramBin> Histogram,
    IReadOnlyList<HistogramBin> DeltaHistogram);

/// <summary>
/// One normalized joystick path sample.
/// </summary>
public sealed record NormalizedPoint(double X, double Y);

/// <summary>
/// Joint statistics for a joystick X/Y signal pair.
/// </summary>
public sealed record JoystickPairStatistics(
    int SampleCount,
    double DurationSeconds,
    double CenterX,
    double CenterY,
    double MeanRadius,
    double MaximumRadius,
    double Percentile95Radius,
    double DeadzonePercent,
    double SaturationPercent,
    double UsedXRangePercent,
    double UsedYRangePercent,
    double BiasX,
    double BiasY,
    double Quadrant1Percent,
    double Quadrant2Percent,
    double Quadrant3Percent,
    double Quadrant4Percent,
    double PathLength,
    double MaximumSpeed);

/// <summary>
/// Analytics result for a joystick X/Y pair.
/// </summary>
public sealed record JoystickPairAnalyticsResult(
    string XSignalLabel,
    string YSignalLabel,
    JoystickPairStatistics Statistics,
    IReadOnlyList<HistogramBin> RadiusHistogram,
    IReadOnlyList<NormalizedPoint> PathPoints);

/// <summary>
/// Ranking row for quickly identifying dynamic/noisy signals.
/// </summary>
public sealed record SignalRankingRow(
    string SignalLabel,
    int SampleCount,
    double DurationSeconds,
    double Range,
    double StandardDeviation,
    int ChangeEvents,
    double ChangesPerSecond,
    double MaximumAbsoluteDelta,
    double MaximumAbsoluteSlope,
    double ActivityScore);

/// <summary>
/// Per-axis tracking quality between a command signal and an actuator response.
/// </summary>
public sealed record AxisTrackingStatistics(
    string AxisName,
    int SampleCount,
    double Correlation,
    double NormalizedRmse,
    double Gain,
    double Offset,
    double MeanAbsoluteError,
    double Percentile95AbsoluteError,
    double MaximumAbsoluteError,
    double DeadzoneMismatchPercent);

/// <summary>
/// Joystick-to-actuator tracking analytics over X/Y pairs.
/// </summary>
public sealed record JoystickActuatorComparisonResult(
    string JoystickXSignalLabel,
    string JoystickYSignalLabel,
    string ActuatorXSignalLabel,
    string ActuatorYSignalLabel,
    AxisTrackingStatistics XAxis,
    AxisTrackingStatistics YAxis,
    double MeanVectorError,
    double Percentile95VectorError,
    double MaximumVectorError,
    IReadOnlyList<HistogramBin> VectorErrorHistogram,
    IReadOnlyList<NormalizedPoint> JoystickPath,
    IReadOnlyList<NormalizedPoint> ActuatorPath);

/// <summary>
/// One point in a lag/correlation curve.
/// </summary>
public sealed record DelayCorrelationPoint(double LagSeconds, double Correlation, int SampleCount);

/// <summary>
/// Generic time/value point for overlay plotting.
/// </summary>
public sealed record TimeValuePoint(double Time, double Value);

/// <summary>
/// Delay analysis between a command signal and a response signal.
/// </summary>
public sealed record DelayAnalysisResult(
    string CommandSignalLabel,
    string ResponseSignalLabel,
    int SampleCount,
    double SearchRangeSeconds,
    double BestLagSeconds,
    double BestCorrelation,
    double MeanAbsoluteErrorShifted,
    double Percentile95AbsoluteErrorShifted,
    double MaximumAbsoluteErrorShifted,
    double? FirstThresholdDelaySeconds,
    int MatchedDelayEvents,
    double? MeanEventDelaySeconds,
    double? MinimumEventDelaySeconds,
    double? MaximumEventDelaySeconds,
    double? Percentile95EventDelaySeconds,
    DelayEventStatistics RisingDelay,
    DelayEventStatistics FallingDelay,
    IReadOnlyList<HistogramBin> DelayHistogram,
    IReadOnlyList<DelayCorrelationPoint> CorrelationCurve,
    IReadOnlyList<TimeValuePoint> CommandSeries,
    IReadOnlyList<TimeValuePoint> ResponseSeries,
    IReadOnlyList<TimeValuePoint> ShiftedResponseSeries);

/// <summary>
/// Dead-time analysis: the time between each command step edge and the first
/// reaction in the feedback signal. Robust against ramped (non-step) feedback,
/// because it measures the onset of motion rather than the steepest change.
/// </summary>
public sealed record FirstResponseDelayResult(
    string CommandSignalLabel,
    string ResponseSignalLabel,
    int CommandEdgeCount,
    int MatchedReactionCount,
    double ResponseThresholdFraction,
    double? MeanDeadTimeSeconds,
    double? MinimumDeadTimeSeconds,
    double? MaximumDeadTimeSeconds,
    double? Percentile95DeadTimeSeconds,
    DelayEventStatistics RisingDeadTime,
    DelayEventStatistics FallingDeadTime,
    IReadOnlyList<HistogramBin> DeadTimeHistogram);

/// <summary>
/// Event-based delay statistics (command -> feedback).
/// </summary>
public sealed record DelayEventStatistics(
    int MatchedEventCount,
    double? MeanDelaySeconds,
    double? MinimumDelaySeconds,
    double? MaximumDelaySeconds,
    double? Percentile95DelaySeconds);

/// <summary>
/// Butterfly-machine joystick vs actuator analytics:
/// joystick (X/Y/Yaw) mapped to actuator Left/Right/Front.
/// </summary>
public sealed record ButterflyKinematicsResult(
    string JoystickXSignalLabel,
    string JoystickYSignalLabel,
    string JoystickYawSignalLabel,
    string ActuatorLeftSignalLabel,
    string ActuatorRightSignalLabel,
    string ActuatorFrontSignalLabel,
    AxisTrackingStatistics LeftTracking,
    AxisTrackingStatistics RightTracking,
    AxisTrackingStatistics FrontTracking,
    DelayEventStatistics LeftDelay,
    DelayEventStatistics RightDelay,
    DelayEventStatistics FrontDelay,
    IReadOnlyList<TimeValuePoint> LeftCommandSeries,
    IReadOnlyList<TimeValuePoint> LeftResponseSeries,
    IReadOnlyList<TimeValuePoint> RightCommandSeries,
    IReadOnlyList<TimeValuePoint> RightResponseSeries,
    IReadOnlyList<TimeValuePoint> FrontCommandSeries,
    IReadOnlyList<TimeValuePoint> FrontResponseSeries,
    IReadOnlyList<HistogramBin> DelayHistogramLeft,
    IReadOnlyList<HistogramBin> DelayHistogramRight,
    IReadOnlyList<HistogramBin> DelayHistogramFront);
