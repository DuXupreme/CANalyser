using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class MotionTrackingTests
{
    private static SignalSeries Signal(string name, Func<double, double> value, int count = 1001)
    {
        var time = Enumerable.Range(0, count).Select(i => i / 100d).ToArray();
        return new(name, time, time.Select(value).ToArray());
    }

    [Fact]
    public void LongRestDoesNotDiluteMovementError()
    {
        var command = Signal("setpoint", t => t >= 1 ? 1 : 0);
        var feedback = Signal("position", t => t >= 1.2 ? 1 : 0);
        var result = MotionTrackingAnalyzer.Analyze(command, feedback, .01, .01);
        Assert.Equal(10, result.CoveredSeconds, 6);
        Assert.InRange(result.MovingSeconds, .19, .21);
        Assert.InRange(result.MeanAbsoluteError!.Value, .49, .51);
        Assert.Equal(1, result.MaximumAbsoluteError);
        Assert.Equal(1, result.WorstTimeSeconds);
    }

    [Fact]
    public void ConstantSignalsDoNotInventMovement()
    {
        var result = MotionTrackingAnalyzer.Analyze(Signal("c", _ => 1), Signal("f", _ => .5), .01, .01);
        Assert.Equal(0, result.MovingSeconds);
        Assert.Null(result.MeanAbsoluteError);
        Assert.Null(result.WorstTimeSeconds);
    }

    [Fact]
    public void SmallGradualMovementAccumulatesPastTolerance()
    {
        var result = MotionTrackingAnalyzer.Analyze(Signal("c", t => t * .001), Signal("f", t => t * .001 - .02), .00005, .00005);
        Assert.True(result.MovingSeconds > 9);
        Assert.Equal(.02, result.MeanAbsoluteError!.Value, 6);
    }

    [Fact]
    public void ImportGapsAndRunStateExcludeUnknownIntervals()
    {
        var command = Signal("c", t => t);
        var feedback = Signal("f", t => t - 1);
        var run = Signal("run", t => t < 5 ? 1 : 0);
        var result = MotionTrackingAnalyzer.Analyze(command, feedback, .01, .01, gaps: [new(2, 3)], runSignal: run);
        Assert.Equal(4, result.CoveredSeconds, 5);
        Assert.InRange(result.MovingSeconds, 3.9, 4);
        Assert.Equal(1, result.MeanAbsoluteError!.Value, 6);
    }
}
