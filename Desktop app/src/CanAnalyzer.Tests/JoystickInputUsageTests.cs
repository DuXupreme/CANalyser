using System.Text.Json;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class JoystickInputUsageTests
{
    private static readonly JoystickAxisCalibration UnitAxis = new(-1, 0, 1);

    private static JoystickUsageResult Analyze(double[] time, double[] x, double[]? y = null,
        double? start = null, double? end = null, IReadOnlyList<MeasurementGap>? gaps = null) =>
        UsageAnalytics.AnalyzeJoystick(new SignalSeries("X", time, x),
            new SignalSeries("Y", time, y ?? new double[time.Length]), start, end,
            gaps: gaps, xCalibration: UnitAxis, yCalibration: UnitAxis);

    private static double[] Times(int count, double step = .1) => Enumerable.Range(0, count).Select(i => i * step).ToArray();

    [Fact]
    public void PhysicalAmplitudeBandsUseMeasuredDurationAndSumToCoverage()
    {
        var result = Analyze([0, 1, 3, 4, 7, 11], [0, .2, .5, .8, 1, 0]);
        Assert.True(result.IsCalibrated);
        Assert.Equal(new[] { 1d, 2, 1, 3, 4 }, result.AmplitudeBands.Select(b => b.Seconds).ToArray());
        Assert.Equal(11, result.CoveredSeconds);
        Assert.Equal(100, result.AmplitudeBands.Sum(b => b.Percent), 8);
        Assert.Equal(result.CoveredSeconds, result.AmplitudeBands.Sum(b => b.Seconds), 8);
    }

    [Fact]
    public void EachAxisUsesItsOwnPhysicalNeutralAndAsymmetricLimits()
    {
        var time = new[] { 0d, 1, 2, 3 };
        var result = UsageAnalytics.AnalyzeJoystick(new SignalSeries("X", time, [-20, 80, -60, -20]),
            new SignalSeries("Y", time, [20, 70, 10, 20]),
            xCalibration: new(-100, -20, 180), yCalibration: new(0, 20, 120));
        Assert.Equal(new[] { 0d, .5, -.5 }, result.Points.Select(p => p.X).ToArray());
        Assert.Equal(new[] { 0d, .5, -.5 }, result.Points.Select(p => p.Y).ToArray());
        Assert.Equal(1, result.AmplitudeBands[0].Seconds);
        Assert.Equal(2, result.AmplitudeBands[2].Seconds);
    }

    [Fact]
    public void DiagonalMediumAxisInputsAreNotReportedAsFullAxisInput()
    {
        var result = Analyze([0, 1, 2], [.65, .65, .65], [.65, .65, .65]);
        Assert.Equal(0, result.AmplitudeBands[4].Seconds);
        Assert.Equal(100, result.AmplitudeBands[3].Percent);
        Assert.Empty(result.FullHolds);
        // The separately labelled legacy radial histogram retains its prior meaning.
        Assert.Equal(100, result.SaturationPercent);
    }

    [Fact]
    public void AShortCorrectionRequiresSmallPeakShortDurationAndObservedReturnToNeutral()
    {
        var time = Times(31);
        var x = time.Select((_, i) => i is >= 2 and < 7 or >= 10 and < 23 ? .2 : 0).ToArray();
        var result = Analyze(time, x);
        Assert.Equal(2, result.InputEpisodes.Count);
        Assert.All(result.InputEpisodes, e => Assert.True(e.IsComplete));
        var correction = Assert.Single(result.InputEpisodes, e => e.IsShortCorrection);
        Assert.Equal(.2, correction.StartSeconds, 8);
        Assert.Equal(.5, correction.DurationSeconds, 8);
        Assert.False(result.InputEpisodes[1].IsShortCorrection);
        Assert.Equal(1.3, result.InputEpisodes[1].DurationSeconds, 8);
    }

    [Fact]
    public void WindowBoundariesDoNotInventNeutralStartsOrReturns()
    {
        var time = Times(21);
        var x = time.Select((_, i) => i is >= 2 and < 7 or >= 14 and < 19 ? .2 : 0).ToArray();
        var result = Analyze(time, x, start: .4, end: 1.6);
        Assert.Equal(2, result.InputEpisodes.Count);
        Assert.All(result.InputEpisodes, e => Assert.False(e.IsShortCorrection));
        Assert.False(result.InputEpisodes[0].HasObservedStart);
        Assert.True(result.InputEpisodes[0].ReturnedToNeutral);
        Assert.True(result.InputEpisodes[1].HasObservedStart);
        Assert.False(result.InputEpisodes[1].ReturnedToNeutral);
        Assert.Equal(1.2, result.CoveredSeconds, 8);
    }

    [Fact]
    public void AnImportGapSplitsEpisodesWithoutCountingAShortCorrectionAcrossMissingData()
    {
        var time = Times(11);
        var x = time.Select((_, i) => i is >= 2 and < 7 ? .2 : 0).ToArray();
        var result = Analyze(time, x, gaps: [new(.4, .5)]);
        Assert.Equal(2, result.InputEpisodes.Count);
        Assert.All(result.InputEpisodes, e => Assert.False(e.IsShortCorrection));
        Assert.True(result.InputEpisodes[0].HasObservedStart);
        Assert.False(result.InputEpisodes[0].ReturnedToNeutral);
        Assert.False(result.InputEpisodes[1].HasObservedStart);
        Assert.True(result.InputEpisodes[1].ReturnedToNeutral);
        Assert.Equal(.9, result.CoveredSeconds, 8);
        Assert.Equal(.9, result.AmplitudeBands.Sum(b => b.Seconds), 8);
    }

    [Fact]
    public void FullHoldsRequireTwoContinuousSecondsAndDoNotBridgeGaps()
    {
        var time = Times(71);
        var x = time.Select((_, i) => i is >= 5 and < 35 or >= 40 and < 55 ? .95 : 0).ToArray();
        var result = Analyze(time, x);
        var hold = Assert.Single(result.FullHolds);
        Assert.Equal(.5, hold.StartSeconds, 8);
        Assert.Equal(3, hold.DurationSeconds, 8);
        var withGap = Analyze(time, x, gaps: [new(1.5, 1.6)]);
        Assert.Empty(withGap.FullHolds);
    }

    [Fact]
    public void SampleGapsAndAsynchronousAxesShareTheSameCoverageInTheNewMetrics()
    {
        var x = new SignalSeries("X", [0, .1, .2, 10, 10.1, 10.2], [0, .2, 0, 0, .2, 0]);
        var y = new SignalSeries("Y", [.05, .15, .25, 10.05, 10.15, 10.25], [0, 0, 0, 0, 0, 0]);
        var result = UsageAnalytics.AnalyzeJoystick(x, y, xCalibration: UnitAxis, yCalibration: UnitAxis);
        Assert.Equal(.3, result.CoveredSeconds, 8);
        Assert.Equal(result.CoveredSeconds, result.AmplitudeBands.Sum(b => b.Seconds), 8);
        Assert.DoesNotContain(result.Points, p => p.Time is > .2 and < 10);
        Assert.DoesNotContain(result.InputEpisodes, e => e.IsShortCorrection);
    }

    [Fact]
    public void UncalibratedDataNeverCreatesPhysicalInputCategoriesOrEvents()
    {
        var time = Times(31);
        var x = time.Select((_, i) => i is >= 2 and < 7 ? .002 : 0).ToArray();
        var result = UsageAnalytics.AnalyzeJoystick(new SignalSeries("X", time, x),
            new SignalSeries("Y", time, new double[time.Length]));
        Assert.False(result.IsCalibrated);
        Assert.NotEmpty(result.Points);
        Assert.Empty(result.AmplitudeBands);
        Assert.Empty(result.InputEpisodes);
        Assert.Empty(result.FullHolds);
    }

    [Fact]
    public void OutOfCalibrationSamplesAreReportedAndNeverClipped()
    {
        var result = Analyze([0, 1, 2], [1.2, -.2, 0]);
        Assert.Equal(1.2, result.Points[0].X, 8);
        Assert.Equal(1, result.OutsideCalibrationSeconds, 8);
        Assert.Equal(1, result.AmplitudeBands[4].Seconds, 8);
        Assert.NotEmpty(JsonSerializer.Serialize(result));
    }

    [Fact]
    public void SharedCalibrationArgumentsRemainCompatible()
    {
        var result = UsageAnalytics.AnalyzeJoystick(new SignalSeries("X", [0, 1, 2], [0, 50, 100]),
            new SignalSeries("Y", [0, 1, 2], [50, 50, 50]), calibratedMinimum: 0, calibratedMaximum: 100);
        Assert.True(result.IsCalibrated);
        Assert.Equal(-1, result.Points[0].X);
        Assert.Equal(0, result.Points[0].Y);
        Assert.Equal(1, result.AmplitudeBands[4].Seconds);
    }

    [Fact]
    public void InvalidAxisCalibrationCannotProducePlausibleNumbers()
    {
        var x = new SignalSeries("X", [0, 1, 2], [0, .2, 0]);
        var y = new SignalSeries("Y", [0, 1, 2], [0, 0, 0]);
        Assert.Throws<ArgumentException>(() => UsageAnalytics.AnalyzeJoystick(x, y, xCalibration: UnitAxis));
        Assert.Throws<ArgumentException>(() => UsageAnalytics.AnalyzeJoystick(x, y,
            xCalibration: new(-1, 2, 1), yCalibration: UnitAxis));
        Assert.Throws<ArgumentException>(() => UsageAnalytics.AnalyzeJoystick(x, y,
            xCalibration: new(-1, double.NaN, 1), yCalibration: UnitAxis));
    }
}
