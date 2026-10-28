using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class MotionResponseReferenceTests
{
    private static SignalSeries Series(string label, Func<double, double> value, double end = 4)
    {
        var times = Enumerable.Range(0, (int)Math.Round(end / .01) + 1).Select(i => Math.Round(i * .01, 9)).ToArray();
        return new SignalSeries(label, times, times.Select(value).ToArray());
    }

    private static MotionResponseOptions PositionOptions(double tolerance = .2) => new()
    {
        Reference = MotionResponseReference.PositionRequest,
        PositionToleranceAbsolute = tolerance,
        ResponseThresholdAbsolute = .05
    };

    [Fact]
    public void DefaultRemainsDescriptiveAndMetadataNamesTheReference()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < 1 ? 0 : 1), Series("r", t => t < 1.2 ? 0 : 1));
        Assert.Equal(MotionResponseReference.SetpointChange, result.Reference);
        Assert.Null(result.PositionTolerance);
        var measurement = Assert.Single(result.Events);
        Assert.Equal(measurement.CommandTimeSeconds, measurement.OriginCommandTimeSeconds);
        Assert.Equal(measurement.CommandSourceIndex, measurement.OriginCommandSourceIndex);
    }

    [Fact]
    public void FallingRampHasDifferentDescriptiveAndNewDirectionalRequestTimes()
    {
        var command = Series("c", t => t <= 1 ? 140 : Math.Max(80, 140 - (t - 1) * 100));
        var response = Series("r", t => t < 1.5 ? 100 : 98);
        var options = PositionOptions(1);
        var descriptive = MotionResponseAnalyzer.Analyze(command, response, options with { Reference = MotionResponseReference.SetpointChange });
        var request = MotionResponseAnalyzer.Analyze(command, response, options);
        var a = Assert.Single(descriptive.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
        var b = Assert.Single(request.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
        Assert.Equal(MotionResponseReference.PositionRequest, request.Reference);
        Assert.Equal(1, request.PositionTolerance);
        Assert.Equal(1.01, a.CommandTimeSeconds, 8);
        Assert.Equal(1.41, b.CommandTimeSeconds, 8);
        Assert.Equal(a.CommandTimeSeconds, b.OriginCommandTimeSeconds);
        Assert.Equal(a.CommandSourceIndex, b.OriginCommandSourceIndex);
        Assert.Equal(1.5, a.ReactionTimeSeconds!.Value, 8);
        Assert.Equal(a.ReactionTimeSeconds, b.ReactionTimeSeconds);
        Assert.Equal(.49, a.DelaySeconds!.Value, 8);
        Assert.Equal(.09, b.DelaySeconds!.Value, 8);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void TargetReductionStillOnSameSideDoesNotBecomeAnOppositeRequest(int sign)
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => 100 + sign * (t < 1 ? 40 : 20)),
            Series("r", _ => 100), PositionOptions(1));
        var excluded = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.NoNewDirectionRequest, excluded.Outcome);
        Assert.Equal(sign, excluded.Direction);
        Assert.Null(excluded.DelaySeconds);
    }

    [Fact]
    public void QuantizedRampRequestsAtToleranceCrossingAndPreservesOriginalEpisode()
    {
        static double Command(double t) => t < 1 ? 0 : Math.Min(1, (Math.Floor((t - 1 + 1e-9) / .03) + 1) * .05);
        var result = MotionResponseAnalyzer.Analyze(Series("c", Command), Series("r", t => t < 1.3 ? 0 : .1), PositionOptions());
        var measured = Assert.Single(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
        Assert.Equal(1.09, measured.CommandTimeSeconds, 8);
        Assert.Equal(1, measured.OriginCommandTimeSeconds!.Value, 8);
        Assert.Equal(109, measured.CommandSourceIndex);
        Assert.Equal(.21, measured.DelaySeconds!.Value, 8);
    }

    [Fact]
    public void AutomaticPositionToleranceDoesNotChangeWithResponseDepartureOverride()
    {
        var command = Series("c", t => t < 1 ? 0 : 1);
        var response = Series("r", t => Math.Clamp(t - 1.2, 0, 1));
        var automatic = MotionResponseAnalyzer.Analyze(command, response,
            new() { Reference = MotionResponseReference.PositionRequest, ResponseThresholdAbsolute = .02 });
        var largerDeparture = MotionResponseAnalyzer.Analyze(command, response,
            new() { Reference = MotionResponseReference.PositionRequest, ResponseThresholdAbsolute = .5 });
        Assert.Equal(automatic.PositionTolerance, largerDeparture.PositionTolerance);
        Assert.Equal(automatic.Events.Select(e => e.CommandTimeSeconds), largerDeparture.Events.Select(e => e.CommandTimeSeconds));
        Assert.NotEqual(automatic.AllStatistics.MeanDelaySeconds, largerDeparture.AllStatistics.MeanDelaySeconds);
    }

    [Fact]
    public void LateToleranceCrossingStartsItsOwnSearchWindow()
    {
        var command = Series("c", t => Math.Clamp(t - 1, 0, 5), end: 7);
        var response = Series("r", t => t < 4.2 ? 0 : .2, end: 7);
        var result = MotionResponseAnalyzer.Analyze(command, response, PositionOptions(3) with { MaxDelaySeconds = .5 });
        var measured = Assert.Single(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
        Assert.Equal(4, measured.CommandTimeSeconds, 8);
        Assert.Equal(1.01, measured.OriginCommandTimeSeconds!.Value, 8);
        Assert.Equal(.2, measured.DelaySeconds!.Value, 8);
    }

    [Fact]
    public void UnrelatedEpisodeAcrossAQuietPauseIsNotLinkedAsOrigin()
    {
        var command = Series("c", t => t < .5 ? 0 : t < 2 ? .1 : .1 + (t - 2) * .001);
        var response = Series("r", _ => 0);
        var result = MotionResponseAnalyzer.Analyze(command, response, PositionOptions(.101));
        var request = Assert.Single(result.Events, e => e.Outcome != MotionResponseOutcome.NoNewDirectionRequest);
        Assert.NotEqual(.5, request.OriginCommandTimeSeconds);
    }

    [Theory]
    [InlineData(MotionResponseReference.SetpointChange)]
    [InlineData(MotionResponseReference.PositionRequest)]
    public void LateOldDirectionMotionAfterNeutralCancellationIsMeasuredFromItsTurningPoint(MotionResponseReference reference)
    {
        var command = Series("c", t => t < 1 ? 0 : t < 1.05 ? 1 : t < 1.1 ? 0 : -1);
        var response = Series("r", t => t < 1.15 ? 0 : t < 1.25 ? (t - 1.15) * 10 : Math.Max(-1, 1 - (t - 1.25) * 10));
        var result = MotionResponseAnalyzer.Analyze(command, response, PositionOptions() with { Reference = reference });
        var reverse = Assert.Single(result.Events, e => Math.Abs(e.CommandTimeSeconds - 1.1) < 1e-9);
        Assert.Equal(MotionResponseKind.Reversal, reverse.Kind);
        Assert.Equal(MotionResponseOutcome.Accepted, reverse.Outcome);
        Assert.Equal(1.26, reverse.ReactionTimeSeconds!.Value, 8);
        Assert.Equal(.16, reverse.DelaySeconds!.Value, 8);
        Assert.Equal(1, reverse.ResponseBaseline!.Value, 8);
    }

    [Theory]
    [InlineData(MotionResponseReference.SetpointChange)]
    [InlineData(MotionResponseReference.PositionRequest)]
    public void BothReferencesKeepAGenuineReversalOfAlreadyMovingFeedback(MotionResponseReference reference)
    {
        var command = Series("c", t => t < .5 ? 0 : t < 1 ? 1 : -1);
        var response = Series("r", t => t < .6 ? 0 : t <= 1.12 ? t - .6 : Math.Max(-1, .52 - (t - 1.12)));
        var result = MotionResponseAnalyzer.Analyze(command, response, PositionOptions() with { Reference = reference });
        var reverse = Assert.Single(result.Events, e => Math.Abs(e.CommandTimeSeconds - 1) < 1e-9);

        Assert.Equal(MotionResponseKind.Reversal, reverse.Kind);
        Assert.Equal(MotionResponseOutcome.Accepted, reverse.Outcome);
        Assert.Equal(1.17, reverse.ReactionTimeSeconds!.Value, 8);
        Assert.Equal(.17, reverse.DelaySeconds!.Value, 8);
        Assert.Equal(.52, reverse.ResponseBaseline!.Value, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsolatedOppositeSpikeAfterNeutralCancellationCannotFabricateReversal(bool laterGenuineResponse)
    {
        var command = Series("c", t => t < 1 ? 0 : t < 1.05 ? 1 : t < 1.1 ? 0 : -1);
        var response = Series("r", t => Math.Abs(t - 1.16) < 1e-9 ? .5 : laterGenuineResponse && t >= 1.4 ? -.1 : 0);
        var result = MotionResponseAnalyzer.Analyze(command, response, PositionOptions());
        var request = Assert.Single(result.Events, e => Math.Abs(e.CommandTimeSeconds - 1.1) < 1e-9);
        Assert.Equal(MotionResponseKind.Start, request.Kind);
        Assert.Equal(0, request.ResponseBaseline);
        if (laterGenuineResponse)
        {
            Assert.Equal(MotionResponseOutcome.Accepted, request.Outcome);
            Assert.Equal(1.4, request.ReactionTimeSeconds!.Value, 8);
            Assert.Equal(.3, request.DelaySeconds!.Value, 8);
        }
        else
        {
            Assert.Equal(MotionResponseOutcome.NoResponseWithinWindow, request.Outcome);
            Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidPositionToleranceIsRejected(double tolerance)
    {
        Assert.Throws<ArgumentException>(() => MotionResponseAnalyzer.Analyze(Series("c", _ => 0), Series("r", _ => 0), PositionOptions(tolerance)));
    }

    [Fact]
    public void OppositeRequestWithQuietFeedbackDoesNotTurnAnIsolatedSpikeIntoAReversal()
    {
        var command = Series("c", t => t < 1 ? 0 : t < 1.1 ? 1 : -1);
        var response = Series("r", t => Math.Abs(t - 1.2) < 1e-9 ? 1 : 0);
        var result = MotionResponseAnalyzer.Analyze(command, response, PositionOptions());
        var reverse = Assert.Single(result.Events, e => Math.Abs(e.CommandTimeSeconds - 1.1) < 1e-9);
        Assert.Equal(MotionResponseKind.Reversal, reverse.Kind);
        Assert.Equal(MotionResponseOutcome.NoResponseWithinWindow, reverse.Outcome);
        Assert.Equal(0, reverse.ResponseBaseline);
        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
    }

    [Fact]
    public void IsolatedLatestPrecommandSpikeDoesNotEstablishOppositeMotion()
    {
        var command = Series("c", t => t < 1 ? 1 : -1);
        var response = Series("r", t => Math.Abs(t - .99) < 1e-9 ? .5 : 0);
        var result = MotionResponseAnalyzer.Analyze(command, response, PositionOptions());
        var reverse = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.AlreadyMoving, reverse.Outcome);
        Assert.Null(reverse.ReactionTimeSeconds);
        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
    }

    [Fact]
    public void SetpointReversalWithQuietFeedbackRejectsAnIsolatedOppositeSpike()
    {
        var command = Series("c", t => t < 1 ? 0 : t < 1.1 ? 1 : -1);
        var response = Series("r", t => Math.Abs(t - 1.2) < 1e-9 ? 1 : 0);
        var result = MotionResponseAnalyzer.Analyze(command, response,
            PositionOptions() with { Reference = MotionResponseReference.SetpointChange });
        var reverse = Assert.Single(result.Events, e => Math.Abs(e.CommandTimeSeconds - 1.1) < 1e-9);

        Assert.Equal(MotionResponseKind.Reversal, reverse.Kind);
        Assert.Equal(MotionResponseOutcome.NoResponseWithinWindow, reverse.Outcome);
        Assert.Equal(0, reverse.ResponseBaseline);
        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
    }

    [Fact]
    public void SetpointChangeRejectsAnIsolatedOppositePrecommandSpike()
    {
        var command = Series("c", t => t < 1 ? 1 : -1);
        var response = Series("r", t => Math.Abs(t - .99) < 1e-9 ? .5 : 0);
        var result = MotionResponseAnalyzer.Analyze(command, response,
            PositionOptions() with { Reference = MotionResponseReference.SetpointChange });
        var reverse = Assert.Single(result.Events);

        Assert.Equal(MotionResponseOutcome.AlreadyMoving, reverse.Outcome);
        Assert.Null(reverse.ReactionTimeSeconds);
        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
    }
}
