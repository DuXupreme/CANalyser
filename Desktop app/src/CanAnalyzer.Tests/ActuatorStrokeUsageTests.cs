using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class ActuatorStrokeUsageTests
{
    [Fact]
    public void Analyze_ReportsRobustTravelAndTimeAtBothExtremes()
    {
        var series = new SignalSeries("Actuator", [0, 1, 2, 3, 4, 5], [0, 25, 50, 75, 100, 100]);

        var result = new ActuatorStrokeUsageAnalyzer().Analyze(series, 0, 100, 5);

        Assert.Equal(6, result.SampleCount);
        Assert.Equal(0, result.ObservedMinimum);
        Assert.Equal(100, result.ObservedMaximum);
        Assert.InRange(result.RobustStrokeUsedPercent, 98, 100);
        Assert.Equal(20, result.TimeNearMinimumPercent, 6);
        Assert.Equal(20, result.TimeNearMaximumPercent, 6);
        Assert.Equal(0, result.TimeOutsideReferencePercent);
        Assert.True(result.ReachedMinimumBand);
        Assert.True(result.ReachedMaximumBand);
    }

    [Fact]
    public void Analyze_UsesRequestedTimeWindowAndFlagsOutOfRangeTime()
    {
        var series = new SignalSeries("Actuator", [0, 1, 2, 3, 4], [-5, 10, 110, 90, 90]);

        var result = new ActuatorStrokeUsageAnalyzer().Analyze(series, 0, 100, 10, 1, 3);

        Assert.Equal(3, result.SampleCount);
        Assert.Equal(10, result.ObservedMinimum);
        Assert.Equal(110, result.ObservedMaximum);
        Assert.Equal(50, result.TimeOutsideReferencePercent, 6);
        Assert.True(result.ReachedMinimumBand);
        Assert.True(result.ReachedMaximumBand);
    }

    [Fact]
    public void Analyze_RejectsInvalidReferenceStroke()
    {
        var series = new SignalSeries("Actuator", [0d], [1d]);
        Assert.Throws<ArgumentException>(() => new ActuatorStrokeUsageAnalyzer().Analyze(series, 10, 10));
    }
}
