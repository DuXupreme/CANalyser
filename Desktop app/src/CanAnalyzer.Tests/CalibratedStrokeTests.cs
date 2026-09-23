using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class CalibratedStrokeTests
{
    private static SignalSeries S(string label, params double[] values) =>
        new(label, Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray(), values);
    private static StrokeReference R(double center = 100, double half = 20) => new(center, half, MaximumGapSeconds: 5);

    [Fact]
    public void NarrowObservedMotionDoesNotBecomeFullAvailableRange()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("position", 98, 102, 100, 100), R());
        Assert.Equal(45, result.MinimumPercent);
        Assert.Equal(55, result.MaximumPercent);
        Assert.Equal(0, result.LowPercent);
        Assert.Equal(0, result.HighPercent);
    }

    [Fact]
    public void StationaryAtKnownLimitStillHasMeaningfulEdgeUsage()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("position", 120, 120, 120), R());
        Assert.Equal(100, result.HighPercent);
        Assert.Equal(1, result.HighVisits);
        Assert.Equal(2, result.LongestHighSeconds);
    }

    [Fact]
    public void CalibrationChangesDoNotInventMotionAndTransitionIsExcluded()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("position", 100, 100, 200, 200, 200),
            R() with { CenterSignal = S("center", 100, 100, 200, 200, 200) });
        Assert.Equal(3, result.CoveredSeconds);
        Assert.Equal(50, result.MinimumPercent);
        Assert.Equal(50, result.MaximumPercent);
    }

    [Fact]
    public void DynamicHalfRangeUsesContemporaneousAvailableRange()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("position", 110, 110, 110),
            R() with { HalfRangeSignal = S("half", 20, 10, 10) });
        Assert.Equal(75, result.MinimumPercent);
        Assert.Equal(100, result.MaximumPercent);
        Assert.Equal(50, result.HighPercent);
    }

    [Fact]
    public void TimeWeightedPercentilesDoNotOverweightDenseSampling()
    {
        var position = new SignalSeries("p", [0d, .01, .02, .03, .04, .2], [80d, 80, 80, 80, 120, 120]);
        var result = CalibratedStrokeAnalytics.Analyze(position, R());
        // The automatic gap rule rejects the sparse interval instead of inventing time.
        Assert.Equal(.04, result.CoveredSeconds, 8);
        var weighted = CalibratedStrokeAnalytics.Analyze(new SignalSeries("p", [0d, 1, 4, 5], [80d, 120, 100, 100]), R());
        Assert.Equal(60, weighted.HighPercent);
        Assert.Equal(0, weighted.P01Percent);
        Assert.Equal(100, weighted.P99Percent);
    }

    [Fact]
    public void RunFilterExcludesCalibrationAndUnknownStateCoverage()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("p", 80, 80, 100, 120, 120),
            R() with { RunSignal = S("run", 0, 0, 1, 1, 1) });
        Assert.Equal(2, result.CoveredSeconds);
        Assert.Equal(0, result.LowPercent);
        Assert.Equal(50, result.HighPercent);
    }

    [Fact]
    public void OutsideRangeIsNotClampedIntoEndpointUse()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("p", 70, 130, 100, 100), R());
        Assert.Equal(200d / 3, result.OutsidePercent!.Value, 8);
        Assert.Equal(0, result.LowPercent);
        Assert.Equal(0, result.HighPercent);
        Assert.Equal(1, result.Distribution.Sum(b => b.Seconds));
    }

    [Fact]
    public void SetpointDemandAndActualLimitUseHaveIndependentCoverage()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("p", 100, 120, 120, 120),
            R() with { Setpoint = S("cmd", 120, 120, 120) });
        Assert.Equal(3, result.CoveredSeconds);
        Assert.Equal(2, result.SetpointCoveredSeconds);
        Assert.Equal(100, result.SetpointHighPercent);
        Assert.Equal(50, result.BothHighPercent);
        Assert.Equal(25, result.MeanAbsoluteErrorPercent);
    }

    [Fact]
    public void DeclaredGapsSplitVisitsAndWindowClipsTime()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("p", 120, 120, 120, 120), R(), .25, 2.75,
            gaps: [new(1, 2)]);
        Assert.Equal(1.5, result.CoveredSeconds);
        Assert.Equal(2, result.HighVisits);
        Assert.Equal(.75, result.LongestHighSeconds);
        Assert.Equal(2.5, result.WindowSeconds);
    }

    [Fact]
    public void MissingReferenceAndInvalidRangeCannotProducePercentages()
    {
        Assert.Throws<ArgumentException>(() => CalibratedStrokeAnalytics.Analyze(S("p", 1, 2), new()));
        Assert.Throws<ArgumentException>(() => CalibratedStrokeAnalytics.Analyze(S("p", 1, 2), new(Center: 0)));
        Assert.Throws<ArgumentException>(() => CalibratedStrokeAnalytics.Analyze(S("p", 1, 2), R(0, 0)));
        var empty = CalibratedStrokeAnalytics.Analyze(S("p", 80, 120), R(), 10, 11);
        Assert.Null(empty.HighPercent);
        Assert.Equal(0, empty.CoveredSeconds);
    }

    [Fact]
    public void RangeSignalDoesNotRequireAnUnusedManualRange()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("p", 120, 120, 120),
            new(Center: 100, HalfRangeSignal: S("half", 20, 20, 20), MaximumGapSeconds: 5));
        Assert.Equal(100, result.HighPercent);
    }

    [Fact]
    public void ShortWindowUsesPrecedingPositionWithoutInventingReferenceCoverage()
    {
        var result = CalibratedStrokeAnalytics.Analyze(S("p", 80, 80, 80), R(), .2, .8);
        Assert.Equal(.6, result.CoveredSeconds, 8);
        Assert.Equal(100, result.LowPercent);
        var missing = CalibratedStrokeAnalytics.Analyze(S("p", 80, 80, 80),
            R() with { CenterSignal = new("center", [1d, 2], [100d, 100]) }, .2, .8);
        Assert.Equal(0, missing.CoveredSeconds);
    }

    [Fact]
    public void KnownJoystickCalibrationPreservesNeutralWithOneSidedUse()
    {
        var result = UsageAnalytics.AnalyzeJoystick(S("x", 0, .25, .5, 0), S("y", 0, 0, 0, 0),
            calibratedMinimum: -1, calibratedMaximum: 1);
        Assert.Equal(0, result.Points[0].X);
        Assert.Equal(100d / 3, result.DeadzonePercent!.Value, 8);
        Assert.Equal(0, result.SaturationPercent);
    }

    [Fact]
    public void JoystickExcludesDeclaredImportGaps()
    {
        var result = UsageAnalytics.AnalyzeJoystick(S("x", 0, 0, 0, 0), S("y", 0, 0, 0, 0),
            gaps: [new(.5, 2.5)]);
        Assert.Equal(1, result.CoveredSeconds);
    }

    [Fact]
    public void FirstResponseDoesNotBridgeAnEarlierGapBeforeCrossing()
    {
        var cmd = S("cmd", 0, 1, 1, 1, 1, 1, 1, 1, 1, 1);
        var rsp = new SignalSeries("rsp", [0d, 1, 2, 8, 9], [0d, 0, 0, 0, 1]);
        var result = new JoystickAnalyticsService().AnalyzeFirstResponseDelay(cmd, rsp, 10);
        Assert.Equal(1, result.CommandEdgeCount);
        Assert.Equal(0, result.MatchedReactionCount);
    }

    [Fact]
    public void ConstantSignalsHaveNoCorrelationLatency()
    {
        Assert.Throws<InvalidOperationException>(() => new JoystickAnalyticsService().AnalyzeDelay(S("c", 1, 1, 1), S("r", 2, 2, 2)));
    }

    [Fact]
    public void LateReactionIsNotReusedForAnEarlierCommand()
    {
        var result = new JoystickAnalyticsService().AnalyzeFirstResponseDelay(
            S("c", 0, 1, 1, 2, 2, 2), S("r", 0, 0, 0, 0, 2, 2), 5);
        Assert.Equal(2, result.CommandEdgeCount);
        Assert.Equal(1, result.MatchedReactionCount);
    }
}
