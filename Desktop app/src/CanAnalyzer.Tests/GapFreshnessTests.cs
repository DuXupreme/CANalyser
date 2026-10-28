using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class GapFreshnessTests
{
    private static readonly MeasurementGap[] Gaps = [new(.4, .6)];

    [Fact]
    public void JoystickNeedsFreshSamplesOnBothAxesAfterAnInteriorGap()
    {
        var x = new SignalSeries("X", [0, 1, 2], [0, 1, 1]);
        var y = new SignalSeries("Y", [0, .8, 2], [0, 1, 1]);
        var result = UsageAnalytics.AnalyzeJoystick(x, y, maximumGapSeconds: 5, gaps: Gaps,
            xCalibration: new(-1, 0, 1), yCalibration: new(-1, 0, 1));
        Assert.Equal(1.4, result.CoveredSeconds, 9);
        Assert.DoesNotContain(result.Points, point => point.Time >= .4 && point.Time < 1);
        Assert.Contains(result.Points, point => point.Time == 1);
    }

    [Fact]
    public void StrokeHoldCannotResumeFromThePreGapValue()
    {
        var position = new SignalSeries("position", [0, 1, 2], [120, 120, 120]);
        var result = CalibratedStrokeAnalytics.Analyze(position,
            new(Center: 100, HalfRange: 20, MaximumGapSeconds: 5), gaps: Gaps);
        Assert.Equal(1.4, result.CoveredSeconds, 9);
        Assert.Equal(2, result.HighVisits);
        Assert.Equal(1, result.LongestHighSeconds, 9);
    }

    [Fact]
    public void StrokeNeedsFreshCalibrationAsWellAsFreshPosition()
    {
        var position = new SignalSeries("position", [0, 1, 2, 3], [120, 120, 120, 120]);
        var center = new SignalSeries("center", [0, 2, 3], [100, 100, 100]);
        var result = CalibratedStrokeAnalytics.Analyze(position,
            new(HalfRange: 20, CenterSignal: center, MaximumGapSeconds: 5), gaps: Gaps);
        Assert.Equal(1.4, result.CoveredSeconds, 9);
    }

    [Fact]
    public void TrackingWaitsForBothSourcesToRefreshAfterAGap()
    {
        var command = new SignalSeries("command", [0, 1, 2], [0, 1, 2]);
        var feedback = new SignalSeries("feedback", [0, .8, 2], [0, .5, 1]);
        var result = MotionTrackingAnalyzer.Analyze(command, feedback, .01, .01, 5, Gaps);
        Assert.Equal(1.4, result.CoveredSeconds, 9);
    }

    [Fact]
    public void TrackingCannotReuseThePreGapRunState()
    {
        var command = new SignalSeries("command", [0, 1, 2, 3], [0, 1, 2, 3]);
        var feedback = new SignalSeries("feedback", [0, 1, 2, 3], [0, .5, 1, 1.5]);
        var run = new SignalSeries("run", [0, 2, 3], [1, 1, 1]);
        var result = MotionTrackingAnalyzer.Analyze(command, feedback, .01, .01, 5, Gaps, run);
        Assert.Equal(1.4, result.CoveredSeconds, 9);
    }
}
