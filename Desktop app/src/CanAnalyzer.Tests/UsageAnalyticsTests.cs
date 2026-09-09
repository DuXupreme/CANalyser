using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class UsageAnalyticsTests
{
    [Fact]
    public void StrokeUsesEachActuatorsFullLogExtremaAndKeepsZero()
    {
        var left = UsageAnalytics.AnalyzeStroke(new SignalSeries("left", [0d, 1, 2, 3], [0d, 2800, 2900, 3000]));
        var right = UsageAnalytics.AnalyzeStroke(new SignalSeries("right", [0d, 1, 2, 3], [1000d, 1100, 1200, 1500]));
        Assert.Equal(0d, left.LogMinimum);
        Assert.Equal(3000d, left.LogMaximum);
        Assert.Equal(100d, left.UsedPercent);
        Assert.Equal(1000d, right.LogMinimum);
        Assert.Equal(1500d, right.LogMaximum);
        Assert.Equal(100d, right.UsedPercent);
        Assert.InRange(left.RobustPercent!.Value, 0, 100);
    }

    [Fact]
    public void WindowUsesFixedFullLogReferenceAndDoesNotFallBackToAllData()
    {
        var series = new SignalSeries("position", [0d, 1, 2, 3, 4], [1000d, 1200, 1500, 1800, 2000]);
        var selected = UsageAnalytics.AnalyzeStroke(series, 1, 3);
        Assert.Equal(1000d, selected.LogMinimum);
        Assert.Equal(2000d, selected.LogMaximum);
        Assert.Equal(1200d, selected.Minimum);
        Assert.Equal(1800d, selected.Maximum);
        Assert.Equal(60d, selected.UsedPercent);
        var empty = UsageAnalytics.AnalyzeStroke(series, 10, 11);
        Assert.Equal(0, empty.SampleCount);
        Assert.Null(empty.UsedPercent);
        Assert.Equal(0d, empty.CoveredSeconds);
    }

    [Fact]
    public void StrokeTimePercentagesUseDurationAndClipBoundaryIntervals()
    {
        var series = new SignalSeries("position", [0d, 1, 4, 5], [0d, 100, 50, 50]);
        var result = UsageAnalytics.AnalyzeStroke(series, 0.5, 4.5);
        Assert.Equal(4d, result.CoveredSeconds);
        Assert.Equal(12.5, result.LowPercent!.Value, 8);
        Assert.Equal(75d, result.HighPercent!.Value, 8);
        Assert.Equal(4d, result.Distribution.Sum(b => b.Seconds), 8);
    }

    [Fact]
    public void StrokeSkipsGapsAndNonFiniteValuesButReportsRealExtrema()
    {
        var series = new SignalSeries("position", [0d, 1, 2, 100, 101, 102, 103], [0d, 100, 50, 20, double.NaN, 80, 100]);
        var result = UsageAnalytics.AnalyzeStroke(series);
        Assert.Equal(6, result.SampleCount);
        Assert.Equal(0d, result.LogMinimum);
        Assert.Equal(100d, result.LogMaximum);
        Assert.Equal(3d, result.CoveredSeconds);
    }

    [Fact]
    public void StationaryOrEmptyActuatorHasNoInventedPercentage()
    {
        var stationary = UsageAnalytics.AnalyzeStroke(new SignalSeries("constant", [0d, 1, 2], [2880d, 2880, 2880]));
        Assert.Equal(2880d, stationary.LogMinimum);
        Assert.Equal(2880d, stationary.LogMaximum);
        Assert.Null(stationary.UsedPercent);
        Assert.Null(stationary.LowPercent);
        Assert.Null(stationary.HighPercent);
        var empty = UsageAnalytics.AnalyzeStroke(new SignalSeries("empty", [], []));
        Assert.Null(empty.LogMinimum);
        Assert.Null(empty.RobustPercent);
    }

    [Fact]
    public void JoystickUsesSameWeightedSamplesForPlotsAndStatistics()
    {
        var x = new SignalSeries("x", [0d, 1, 4, 5, 6], [-1d, 0, 1, 0, -1]);
        var y = new SignalSeries("y", [0d, 1, 4, 5, 6], [0d, 0, 0, 0, 0]);
        var result = UsageAnalytics.AnalyzeJoystick(x, y);
        Assert.Equal(6d, result.CoveredSeconds);
        Assert.Equal(100d * 4 / 6, result.DeadzonePercent!.Value, 8);
        Assert.Equal(100d * 2 / 6, result.SaturationPercent!.Value, 8);
        Assert.Equal(result.CoveredSeconds, result.Points.Sum(p => p.Seconds), 8);
        Assert.Equal(result.CoveredSeconds, result.RadiusDistribution.Sum(p => p.Seconds), 8);
    }

    [Fact]
    public void SharedJoystickGapIsExcludedWithoutDiscardingValidSegments()
    {
        var t = new[] { 0d, 1, 2, 100, 101, 102 };
        var result = UsageAnalytics.AnalyzeJoystick(new SignalSeries("x", t, [-1d, 0, 1, 0, -1, 1]), new SignalSeries("y", t, [0d, 0, 0, 0, 0, 0]));
        Assert.Equal(4d, result.CoveredSeconds);
        Assert.Equal(102d, result.WindowSeconds);
        Assert.DoesNotContain(result.Points, p => p.Time is >= 2 and < 100);
        Assert.Equal(4d, result.RadiusDistribution.Sum(b => b.Seconds));
    }

    [Fact]
    public void JoystickWindowPreservesNormalizationAndNeverReplacesEmptySelection()
    {
        var x = new SignalSeries("x", [0d, 1, 2, 3], [-100d, 0, 100, 0]);
        var y = new SignalSeries("y", [0d, 1, 2, 3], [0d, 0, 0, 0]);
        var window = UsageAnalytics.AnalyzeJoystick(x, y, 1, 2);
        Assert.Single(window.Points);
        Assert.Equal(0d, window.Points[0].X);
        Assert.Equal(100d, window.DeadzonePercent);
        Assert.Empty(UsageAnalytics.AnalyzeJoystick(x, y, 10, 11).Points);
    }

    [Fact]
    public void DuplicateTimesAndAsynchronousAxesHaveNoInventedDuration()
    {
        var x = new SignalSeries("x", [0d, 0, 1, 2, 3], [100d, 0, 50, 100, 50]);
        var y = new SignalSeries("y", [0.5d, 1.5, 2.5], [0d, 0, 0]);
        var result = UsageAnalytics.AnalyzeJoystick(x, y);
        Assert.Equal(2d, result.CoveredSeconds);
        Assert.All(result.Points, p => Assert.True(p.Seconds > 0));
        Assert.Equal(-1d, result.Points[0].X);
    }

    [Fact]
    public void JoystickDoesNotBridgeNonFiniteSamples()
    {
        var x = new SignalSeries("x", [0d, 1, 2, 3, 4], [0d, double.PositiveInfinity, 1, 0, -1]);
        var y = new SignalSeries("y", [0d, 1, 2, 3, 4], [0d, 0, 0, 0, 0]);
        var result = UsageAnalytics.AnalyzeJoystick(x, y);
        Assert.Equal(2d, result.CoveredSeconds);
        Assert.All(result.Points, p => Assert.True(double.IsFinite(p.X)));
    }

    [Fact]
    public void CoverageIncludesSelectedTimeOutsideTheCommonSignalSpan()
    {
        var x = new SignalSeries("x", [0d, 1, 2, 3], [-1d, 0, 1, 0]);
        var y = new SignalSeries("y", [1d, 2, 3, 4], [0d, 0, 0, 0]);
        var result = UsageAnalytics.AnalyzeJoystick(x, y, 0, 5);
        Assert.Equal(5d, result.WindowSeconds);
        Assert.Equal(2d, result.CoveredSeconds);
    }
}
