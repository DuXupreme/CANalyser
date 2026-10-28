namespace CanAnalyzer.Core.Domain;

public enum BatteryCountSource { Unknown, ActivePackCountSignal, ConfirmedManual }

/// <summary>A count is held only between fresh observations, never across an import gap.</summary>
public sealed record BatteryCountInterval(double StartSeconds, double EndSeconds, int? ActivePackCount)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
    public string CountLabel => ActivePackCount?.ToString() ?? "onbekend";
}

public sealed record BatteryCountObservation(double SampleTimeSeconds, int ActivePackCount);

public sealed record BatteryConfigurationResult
{
    public required BatteryCountSource Source { get; init; }
    public string? SignalLabel { get; init; }
    public int? ConfirmedManualCount { get; init; }
    public required double StartSeconds { get; init; }
    public required double EndSeconds { get; init; }
    public required IReadOnlyList<BatteryCountInterval> Intervals { get; init; }
    /// <summary>First valid observation and each different count; may include a fresh baseline just before the selected window.</summary>
    public required IReadOnlyList<BatteryCountObservation> ObservedConfigurations { get; init; }
    public double KnownSeconds => Intervals.Where(x => x.ActivePackCount.HasValue).Sum(x => x.DurationSeconds);
    public double CoveragePercent => 100 * KnownSeconds / (EndSeconds - StartSeconds);
    public int? MinimumCount => ObservedConfigurations.Select(x => (int?)x.ActivePackCount).Min();
    public int? MaximumCount => ObservedConfigurations.Select(x => (int?)x.ActivePackCount).Max();
    // A different count after a gap proves a different observed configuration, not the exact change time.
    public int ObservedChanges => Math.Max(0, ObservedConfigurations.Count - 1);
    public bool HasConfigurationChanges => MinimumCount.HasValue && MinimumCount != MaximumCount;
}
