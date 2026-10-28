using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

/// <summary>Independent checks of request direction and attribution, separate from the detector's own tests.</summary>
public sealed class PositionRequestAdversarialTests
{
    private static MotionResponseOptions RequestOptions(double tolerance = .2, double responseThreshold = .05) => new()
    {
        Reference = MotionResponseReference.PositionRequest,
        PositionToleranceAbsolute = tolerance,
        ResponseThresholdAbsolute = responseThreshold,
        MaxDelaySeconds = .8
    };

    private static SignalSeries Series(string label, Func<double, double> value, double step = .01, double end = 3)
    {
        var time = Enumerable.Range(0, (int)Math.Round(end / step) + 1).Select(i => Math.Round(i * step, 9)).ToArray();
        return new SignalSeries(label, time, time.Select(value).ToArray());
    }

    private static MotionResponseEvent[] Requests(MotionResponseResult result) => result.Events
        .Where(e => e.Outcome != MotionResponseOutcome.NoNewDirectionRequest).ToArray();

    [Fact]
    public void FixedTargetWithFeedbackDriftAndOvershoot_CannotCreateACommandRequest()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", _ => 1),
            Series("feedback", t => t < 1 ? 1 : t < 1.5 ? 1.5 : .5), RequestOptions());
        Assert.Empty(Requests(result));
        Assert.Equal(0, result.AllStatistics.MatchedEventCount);
    }

    [Fact]
    public void FallingTargetStillAheadOfPosition_IsNotANegativeRequestOrNewStart()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => t < 1 ? 20 : 15),
            Series("feedback", t => t < 1.2 ? 10 : 15), RequestOptions());
        Assert.Empty(Requests(result));
        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(3)]
    public void ChangedTargetInSameOutstandingDirection_DoesNotRestartTheRequestClock(double secondTarget)
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => t < 1 ? 0 : t < 1.1 ? 2 : secondTarget),
            Series("feedback", t => t < 1.25 ? 0 : secondTarget), RequestOptions());
        var request = Assert.Single(Requests(result));
        Assert.Equal(1, request.CommandTimeSeconds, 8);
        Assert.Equal(1, request.Direction);
        Assert.Equal(MotionResponseOutcome.Accepted, request.Outcome);
        Assert.Equal(.25, request.DelaySeconds!.Value, 8);
    }

    [Fact]
    public void FeedbackOvershootAfterARequest_DoesNotInventASecondReversalCommand()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => t < 1 ? 0 : 1),
            Series("feedback", t => t < 1.2 ? 0 : 1.5), RequestOptions());
        var request = Assert.Single(Requests(result));
        Assert.Equal(1, request.Direction);
        Assert.Equal(MotionResponseOutcome.Accepted, request.Outcome);
    }

    [Fact]
    public void SetpointChangeWhollyWithinPositionMargin_HasOnlyDescriptiveTiming()
    {
        var command = Series("command", t => t < 1 ? 0 : .1);
        var feedback = Series("feedback", t => t < 1.2 ? 0 : .1);
        var descriptive = MotionResponseAnalyzer.Analyze(command, feedback,
            RequestOptions() with { Reference = MotionResponseReference.SetpointChange });
        var positionRequest = MotionResponseAnalyzer.Analyze(command, feedback, RequestOptions());
        Assert.Equal(1, descriptive.AllStatistics.MatchedEventCount);
        Assert.Empty(Requests(positionRequest));
        Assert.DoesNotContain(positionRequest.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
    }

    [Fact]
    public void GradualCommandStartsRequestAtMarginDeparture_NotAtFirstTinyChange()
    {
        var command = Series("command", t => Math.Clamp(t - 1, 0, 1));
        var feedback = Series("feedback", t => t < 1.5 ? 0 : .1);
        var result = MotionResponseAnalyzer.Analyze(command, feedback, RequestOptions());
        var request = Assert.Single(Requests(result));
        Assert.InRange(request.CommandTimeSeconds, 1.199999, 1.210001);
        Assert.Equal(MotionResponseOutcome.Accepted, request.Outcome);
        Assert.Equal(1.5, request.ReactionTimeSeconds!.Value, 8);
        Assert.Equal(1.5 - request.CommandTimeSeconds, request.DelaySeconds!.Value, 8);
    }

    [Fact]
    public void SmallFeedbackNoiseCannotCertifyARequestResponse()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => t < 1 ? 0 : 1),
            Series("feedback", t => .01 * Math.Sin(t * 170)), RequestOptions());
        Assert.NotEmpty(Requests(result));
        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
        Assert.Null(result.AllStatistics.MeanDelaySeconds);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1000, 10)]
    [InlineData(-120, .1)]
    public void SharedReferenceAndMatchedUnitsPreserveRequestTiming(double offset, double scale)
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => offset + (t < 1 ? 0 : scale)),
            Series("feedback", t => offset + (t < 1.2 ? 0 : scale)), RequestOptions(.2 * scale, .05 * scale));
        var request = Assert.Single(Requests(result));
        Assert.Equal(MotionResponseOutcome.Accepted, request.Outcome);
        Assert.Equal(1, request.CommandTimeSeconds, 8);
        Assert.Equal(.2, request.DelaySeconds!.Value, 8);
        Assert.Equal(1, request.Direction);
    }

    [Fact]
    public void RequestDirectionUsesPastFeedback_NotAFutureInterpolatedPosition()
    {
        var command = Series("command", t => t < 1 ? 0 : .5);
        var times = Enumerable.Range(0, 76).Select(i => Math.Round(.02 + i * .04, 9)).ToArray();
        var feedback = new SignalSeries("feedback", times, times.Select(t => t < 1.02 ? 0d : 1d).ToArray());
        var result = MotionResponseAnalyzer.Analyze(command, feedback, RequestOptions(.1));
        var request = Assert.Single(Requests(result));
        Assert.Equal(1, request.Direction);
        Assert.Equal(MotionResponseOutcome.Accepted, request.Outcome);
        Assert.Equal(1, request.CommandTimeSeconds, 8);
        Assert.Equal(1.02, request.ReactionTimeSeconds!.Value, 8);
    }

    [Fact]
    public void FeedbackAlreadyAtTheNewTargetBeforeCommand_IsNeverANewResponse()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => t < 1 ? 0 : 1),
            Series("feedback", t => t < .98 ? 0 : 1), RequestOptions());
        Assert.Empty(Requests(result));
        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
    }

    [Fact]
    public void WithdrawnTargetCannotClaimMovementAfterCancellation()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => t < 1 ? 0 : t < 1.1 ? 1 : 0),
            Series("feedback", t => t < 1.2 ? 0 : 1), RequestOptions());
        var original = Assert.Single(Requests(result), e => Math.Abs(e.CommandTimeSeconds - 1) < 1e-8);
        Assert.NotEqual(MotionResponseOutcome.Accepted, original.Outcome);
        Assert.True(original.ResponseSearchEndSeconds <= 1.1 + 1e-8);
        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
    }

    [Fact]
    public void CancellationDuringConfirmationCannotCertifyTheEarlierThresholdCrossing()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => t < 1 ? 0 : t < 1.09 ? 1 : .1),
            Series("feedback", t => t < 1.08 ? 0 : .1), RequestOptions());
        var original = Assert.Single(Requests(result), e => Math.Abs(e.CommandTimeSeconds - 1) < 1e-8);
        Assert.NotEqual(MotionResponseOutcome.Accepted, original.Outcome);
        Assert.True(original.ResponseSearchEndSeconds <= 1.09 + 1e-8);
        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
    }

    [Fact]
    public void FeedbackCrossingBothSidesOfHysteresisCannotRearmAnUnchangedCommand()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => t < 1 ? 0 : 1),
            Series("feedback", t => t < 1.2 ? 0 : t < 1.4 ? 1 : 1 + .4 * Math.Sin((t - 1.4) * 20)), RequestOptions());
        var request = Assert.Single(Requests(result));
        Assert.Equal(1, request.CommandTimeSeconds, 8);
        Assert.Equal(1, request.Direction);
    }

    [Fact]
    public void LateOppositeMotionCannotTurnReversalIntoReturnPastOldPosition()
    {
        static double Feedback(double t) => t < 1.15 ? 0 : t <= 1.25 ? (t - 1.15) * 10 : Math.Max(-1, 1 - (t - 1.25) * 10);
        var result = MotionResponseAnalyzer.Analyze(Series("command", t => t < 1 ? 0 : t < 1.1 ? 1 : -1),
            Series("feedback", Feedback), RequestOptions());
        var reversal = Assert.Single(Requests(result), e => e.Direction < 0);
        if (reversal.Outcome == MotionResponseOutcome.Accepted)
        {
            Assert.InRange(reversal.ReactionTimeSeconds!.Value, 1.259999, 1.260001);
            Assert.InRange(reversal.ResponseBaseline!.Value, .999999, 1.000001);
        }
        else Assert.Null(reversal.DelaySeconds); // Explicitly unresolved attribution is preferable to the wrong crossing.
    }
}
