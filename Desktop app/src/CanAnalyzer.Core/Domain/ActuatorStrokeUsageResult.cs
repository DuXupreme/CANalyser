namespace CanAnalyzer.Core.Domain;

/// <summary>Robust actuator travel and end-stop usage over a selected measurement interval.</summary>
public sealed record ActuatorStrokeUsageResult(
    string SignalLabel,
    int SampleCount,
    double ReferenceMinimum,
    double ReferenceMaximum,
    double ObservedMinimum,
    double Percentile01,
    double Median,
    double Percentile99,
    double ObservedMaximum,
    double RobustStrokeUsedPercent,
    double TimeNearMinimumPercent,
    double TimeNearMaximumPercent,
    double TimeOutsideReferencePercent,
    bool ReachedMinimumBand,
    bool ReachedMaximumBand);
