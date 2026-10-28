using System.Text.Json;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class BatteryConfigurationTests
{
    private readonly BatteryConfigurationAnalyzer _analyzer = new();

    [Fact]
    public void NoSource_RemainsUnknown_AndManualCountIsExplicit()
    {
        var unknown = _analyzer.Analyze(null, null, 10, 20, 5);
        Assert.Equal(BatteryCountSource.Unknown, unknown.Source);
        Assert.Null(unknown.MinimumCount);
        Assert.Equal(0, unknown.CoveragePercent);
        Assert.Equal(new BatteryCountInterval(10, 20, null), Assert.Single(unknown.Intervals));

        var manual = _analyzer.Analyze(null, 3, 10, 20, 5);
        Assert.Equal(BatteryCountSource.ConfirmedManual, manual.Source);
        Assert.Equal(3, manual.ConfirmedManualCount);
        Assert.Equal(100, manual.CoveragePercent);
        Assert.False(manual.HasConfigurationChanges);
    }

    [Fact]
    public void CountIsStepHeld_AndReportsChangesWithoutInterpolatingCounts()
    {
        var counts = new SignalSeries("BMS.ActivePackCount", [0, 1, 2, 3, 4], [2, 2, 3, 3, 3]);
        var result = _analyzer.Analyze(counts, null, 0.5, 3.5, 2);
        Assert.Equal(2, result.MinimumCount);
        Assert.Equal(3, result.MaximumCount);
        Assert.Equal(1, result.ObservedChanges);
        Assert.Equal(100, result.CoveragePercent);
        Assert.Equal(new[] { new BatteryCountInterval(0.5, 2, 2), new BatteryCountInterval(2, 3.5, 3) }, result.Intervals);
        var json = JsonSerializer.SerializeToElement(result);
        Assert.Equal(counts.Label, json.GetProperty("SignalLabel").GetString());
        Assert.Equal(2, json.GetProperty("Intervals").GetArrayLength());
    }

    [Fact]
    public void ImportGapRequiresANewCountObservation_AndNeverInfersTheChangeTime()
    {
        var counts = new SignalSeries("Count", [0, 1, 2, 3, 4], [2, 2, 3, 3, 3]);
        var result = _analyzer.Analyze(counts, null, 0, 4, 2,
            [new MeasurementGap(1.5, 1.75), new MeasurementGap(1.25, 1.6)]);
        Assert.Equal(new[] { new BatteryCountInterval(0, 1.25, 2), new BatteryCountInterval(1.25, 2, null),
            new BatteryCountInterval(2, 4, 3) }, result.Intervals);
        Assert.Equal(3.25, result.KnownSeconds, 8);
        Assert.Equal(1, result.ObservedChanges);
        Assert.True(result.HasConfigurationChanges);
    }

    [Fact]
    public void SparseCountsAndTrailingCoverage_RemainUnknown()
    {
        var counts = new SignalSeries("Count", [0, 1, 10, 11], [2, 2, 3, 3]);
        var result = _analyzer.Analyze(counts, null, -1, 12, 2);
        Assert.Equal(2, result.KnownSeconds);
        Assert.Equal(13, result.Intervals.Sum(x => x.DurationSeconds));
        Assert.Equal(new BatteryCountInterval(1, 10, null), result.Intervals[2]);
        Assert.Equal(new BatteryCountInterval(11, 12, null), result.Intervals[^1]);
        Assert.Equal(1, result.ObservedChanges);
    }

    [Fact]
    public void ChangeAtFinalSample_IsReportedWithoutInventingDuration()
    {
        var counts = new SignalSeries("Count", [0, 1, 2], [2, 2, 3]);
        var result = _analyzer.Analyze(counts, null, 0, 2, 2);
        Assert.Equal(new BatteryCountInterval(0, 2, 2), Assert.Single(result.Intervals));
        Assert.Equal(2, result.KnownSeconds);
        Assert.True(result.HasConfigurationChanges);
        Assert.Equal(3, result.MaximumCount);
        Assert.Equal(new BatteryCountObservation(2, 3), result.ObservedConfigurations[^1]);
    }

    [Fact]
    public void NonIntegerNegativeAndNonFiniteCounts_AreUnknown_NotRounded()
    {
        var counts = new SignalSeries("Count", [0, 1, 2, 3, 4, 5, 6, 7],
            [double.NaN, -1, 2.5, double.PositiveInfinity, 2, 2, 0, 0]);
        var result = _analyzer.Analyze(counts, null, 0, 7, 2);
        Assert.Equal(new BatteryCountInterval(0, 4, null), result.Intervals[0]);
        Assert.Equal(3, result.KnownSeconds);
        Assert.Equal(0, result.MinimumCount);
        Assert.Equal(2, result.MaximumCount);
    }

    [Fact]
    public void InvalidManualCountOrAmbiguousInputs_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(null, 0, 0, 10, 1));
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(null, -1, 0, 10, 1));
        var counts = new SignalSeries("Count", [0, 1], [2, 2]);
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(counts, 2, 0, 10, 1));
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(counts, null, 0, 10, double.NaN));
    }

    [Fact]
    public void ContradictoryDuplicateTimes_AreRejected()
    {
        var counts = new SignalSeries("Count", [0, 1, 1, 2], [2, 2, 3, 3]);
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(counts, null, 0, 2, 2));
    }

    [Fact]
    public void ChangedConfigurationBlocksRuntime_UnknownAndManualPreserveExistingResults()
    {
        var time = Enumerable.Range(0, 101).Select(x => (double)x).ToArray();
        var rpm = new SignalSeries("RPM", time, time.Select(_ => 1000d).ToArray());
        var soc = new SignalSeries("SOC", time, time.Select(t => 100 - t / 10).ToArray());
        var original = new ActiveUsageAnalyzer().Analyze(rpm, soc, null,
            new ActiveUsageOptions { MinimumActivitySeconds = 0, BridgePauseSeconds = 0 });
        Assert.NotNull(original.ActiveRuntimeHours);
        foreach (var manualCount in new int?[] { null, 3 })
        {
            var same = original with { BatteryConfiguration = _analyzer.Analyze(null, manualCount, 0, 100, 5) };
            Assert.Equal(original.ActiveRuntimeHours, same.ActiveRuntimeHours);
            Assert.Equal(original.MixedRuntimeHours, same.MixedRuntimeHours);
            Assert.Equal(original.TotalEnergy, same.TotalEnergy);
            Assert.Equal(15, same.Options.BatteryCapacityKwh);
        }
        var counts = new SignalSeries("Count", time, time.Select(t => t < 50 ? 2d : 3d).ToArray());
        var changed = original with { BatteryConfiguration = _analyzer.Analyze(counts, null, 0, 100, 5) };
        Assert.False(changed.CanProject);
        Assert.Null(changed.ActiveRuntimeHours);
        Assert.Null(changed.MixedRuntimeHours);
        Assert.Null(changed.RemainingMixedHours);
        Assert.Equal(original.ActiveSeconds, changed.ActiveSeconds);
    }
}
