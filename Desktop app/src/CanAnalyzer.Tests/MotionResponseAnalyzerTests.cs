using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class MotionResponseAnalyzerTests
{
    [Fact]
    public void SmallCommandsRemainVisibleBesideLargeCommandsAndUseOriginalSamples()
    {
        var command = Series("command", t => t < .5 ? 0 : t < 1.5 ? .2 : t < 2.5 ? 100 : 100.2);
        var response = Series("response", t => t < .6 ? 0 : t < 1.6 ? .2 : t < 2.6 ? 100 : 100.2);

        var result = MotionResponseAnalyzer.Analyze(command, response);

        Assert.Equal(3, result.StartStatistics.MatchedEventCount);
        Assert.Equal(3, result.Events.Count);
        Assert.InRange(result.CommandThreshold, .099999, .100001);
        Assert.InRange(result.ResponseThreshold, .199999, .200001);
        Assert.All(result.Events, e =>
        {
            Assert.Equal(MotionResponseOutcome.Accepted, e.Outcome);
            Assert.InRange(e.DelaySeconds!.Value, .099999, .100001);
            Assert.Equal(command.Time[e.CommandSourceIndex], e.CommandTimeSeconds);
            Assert.Equal(response.Time[e.ReactionSourceIndex!.Value], e.ReactionTimeSeconds);
            Assert.True(e.DelayLowerBoundSeconds <= e.DelaySeconds);
            Assert.True(e.DelayUpperBoundSeconds >= e.DelaySeconds);
        });
        Assert.InRange(result.AllStatistics.MeanDelaySeconds!.Value, .099999, .100001);
    }

    [Fact]
    public void GradualStartIsOneEpisodeAndItsDelayDoesNotRequireALargeStep()
    {
        static double Ramp(double t) => Math.Clamp(t - 1, 0, .5);
        var result = MotionResponseAnalyzer.Analyze(Series("c", Ramp), Series("r", t => Ramp(t - .12)));

        var measured = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.Accepted, measured.Outcome);
        Assert.Equal(MotionResponseKind.Start, measured.Kind);
        Assert.InRange(measured.DelaySeconds!.Value, .119999, .120001);
    }

    [Fact]
    public void QuantizedRampIsNotMistakenForManyNewCommandsOrForNoise()
    {
        static double Ramp(double t) => t < .5 ? 0 : Math.Min(20, Math.Floor((t - .5 + 1e-8) / .03) + 1);
        var result = MotionResponseAnalyzer.Analyze(Series("c", Ramp), Series("r", t => Ramp(t - .12)));

        var measured = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.Accepted, measured.Outcome);
        Assert.InRange(measured.DelaySeconds!.Value, .119999, .120001);
        Assert.Equal(1, result.ResponseThreshold, 6);
    }

    [Fact]
    public void NoiseAndOneSampleFeedbackSpikeDoNotProduceReaction()
    {
        var response = Series("r", t => .01 * Math.Sin(t * 170));
        response.Value[60] = .5;
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < .5 ? 0 : 1), response,
            new() { MaxDelaySeconds = .3 });

        Assert.DoesNotContain(result.Events, e => e.Outcome == MotionResponseOutcome.Accepted);
        Assert.Equal(MotionResponseOutcome.NoResponseWithinWindow, Assert.Single(result.Events).Outcome);
        Assert.True(result.ResponseThreshold > .02);
    }

    [Fact]
    public void NoiseOnlyCommandDoesNotProduceEvents()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => .01 * Math.Sin(t * 170)),
            Series("r", t => .01 * Math.Cos(t * 170)));
        Assert.Empty(result.Events);
        Assert.Null(result.AllStatistics.MeanDelaySeconds);
    }

    [Fact]
    public void NoisyRampRequiresSustainedMovementAboveTheAutomaticNoiseFloor()
    {
        var response = Series("r", t => Math.Clamp((t - .7) * 10, 0, 1) + .01 * Math.Sin(t * 170));
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < .5 ? 0 : 1), response);

        var measured = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.Accepted, measured.Outcome);
        Assert.InRange(measured.ReactionTimeSeconds!.Value, .70, .73);
        Assert.True(result.ResponseThreshold > .02);
    }

    [Fact]
    public void FeedbackAlreadyMovingInRequestedDirectionIsExcluded()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < 1 ? 0 : 1), Series("r", t => t));

        var measured = Assert.Single(result.Events);
        Assert.Equal(MotionResponseKind.Correction, measured.Kind);
        Assert.Equal(MotionResponseOutcome.AlreadyMoving, measured.Outcome);
        Assert.Null(measured.DelaySeconds);
        Assert.Equal(0, result.AllStatistics.MatchedEventCount);
    }

    [Theory]
    [InlineData(.01)]
    [InlineData(.02)]
    public void FeedbackStepJustBeforeCommandIsNeverReusedAsZeroDelay(double lead)
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < 1 ? 0 : 1),
            Series("r", t => t < 1 - lead - 1e-9 ? 0 : 1));
        var measured = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.AlreadyMoving, measured.Outcome);
        Assert.Null(measured.DelaySeconds);
        Assert.Equal(0, result.AllStatistics.MatchedEventCount);
    }

    [Fact]
    public void SimultaneousSourceSamplesExposeSamplingIntervalInsteadOfSubsamplePrecision()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < 1 ? 0 : 1),
            Series("r", t => t < 1 ? 0 : 1));
        var measured = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.Accepted, measured.Outcome);
        Assert.Equal(0, measured.DelaySeconds);
        Assert.Equal(0, measured.DelayLowerBoundSeconds);
        Assert.Equal(.01, measured.DelayUpperBoundSeconds!.Value, 8);
    }

    [Fact]
    public void ReversalMeasuresDepartureFromTurningPointInsteadOfReturnPastOldPosition()
    {
        static double Command(double t) => t < .5 ? 0 : t < 1 ? t - .5 : 1.5 - t;
        static double Response(double t) => t < .6 ? 0 : t <= 1.12 ? t - .6 : .52 - (t - 1.12);
        var result = MotionResponseAnalyzer.Analyze(Series("c", Command), Series("r", Response));

        var reversed = Assert.Single(result.Events, e => e.Kind == MotionResponseKind.Reversal);
        Assert.Equal(MotionResponseOutcome.Accepted, reversed.Outcome);
        Assert.InRange(reversed.CommandTimeSeconds, 1.00999, 1.01001);
        Assert.InRange(reversed.ReactionTimeSeconds!.Value, 1.12999, 1.13001);
        Assert.InRange(reversed.DelaySeconds!.Value, .11999, .12001);
        Assert.InRange(reversed.ResponseBaseline!.Value, .51999, .52001);
        Assert.Equal(1, result.StartStatistics.MatchedEventCount);
        Assert.Equal(1, result.ReversalStatistics.MatchedEventCount);
    }

    [Fact]
    public void NewCommandStopsAttributionOfLaterReactionToPreviousCommand()
    {
        var result = MotionResponseAnalyzer.Analyze(
            Series("c", t => t < .5 ? 0 : t < .65 ? 1 : 2),
            Series("r", t => t < .75 ? 0 : 2), new() { MaxDelaySeconds = .5 });

        Assert.Equal(2, result.Events.Count);
        Assert.Equal(MotionResponseOutcome.InterruptedByCommand, result.Events[0].Outcome);
        Assert.Equal(MotionResponseOutcome.Accepted, result.Events[1].Outcome);
        Assert.InRange(result.Events[1].DelaySeconds!.Value, .099999, .100001);
    }

    [Fact]
    public void CloseRenewedCommandAlsoStopsAttributionBeforeTheQuietTimeout()
    {
        var result = MotionResponseAnalyzer.Analyze(
            Series("c", t => t < .5 ? 0 : t < .55 ? 1 : 2),
            Series("r", t => t < .65 ? 0 : 2), new() { MaxDelaySeconds = .5 });
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(MotionResponseOutcome.InterruptedByCommand, result.Events[0].Outcome);
        Assert.Equal(MotionResponseOutcome.Accepted, result.Events[1].Outcome);
        Assert.Equal(MotionResponseKind.Correction, result.Events[1].Kind);
        Assert.InRange(result.Events[1].DelaySeconds!.Value, .099999, .100001);
    }

    [Fact]
    public void SlowReactionIsVisibleAsUnmatchedWithinShortSearchWindow()
    {
        var command = Series("c", t => t < .5 ? 0 : 1);
        var response = Series("r", t => t < .85 ? 0 : 1);
        var shortResult = MotionResponseAnalyzer.Analyze(command, response, new() { MaxDelaySeconds = .2 });
        var fullResult = MotionResponseAnalyzer.Analyze(command, response, new() { MaxDelaySeconds = .5 });

        Assert.Equal(MotionResponseOutcome.NoResponseWithinWindow, Assert.Single(shortResult.Events).Outcome);
        Assert.Null(shortResult.AllStatistics.MeanDelaySeconds);
        Assert.InRange(Assert.Single(fullResult.Events).DelaySeconds!.Value, .349999, .350001);
    }

    [Fact]
    public void SourceGapsAndDeclaredGapsInvalidateEventsWithoutBridgingThem()
    {
        var command = Series("c", t => t < .5 ? 0 : 1);
        var complete = Series("r", t => t < .8 ? 0 : 1);
        var retained = Enumerable.Range(0, complete.Time.Length).Where(i => complete.Time[i] <= .55 || complete.Time[i] >= .75).ToArray();
        var gapped = new SignalSeries("r", retained.Select(i => complete.Time[i]).ToArray(), retained.Select(i => complete.Value[i]).ToArray());
        var sourceGap = MotionResponseAnalyzer.Analyze(command, gapped);
        var declaredGap = MotionResponseAnalyzer.Analyze(command, complete, gaps: [new(.55, .75)]);

        Assert.Equal(MotionResponseOutcome.MeasurementGap, Assert.Single(sourceGap.Events).Outcome);
        Assert.Equal(MotionResponseOutcome.MeasurementGap, Assert.Single(declaredGap.Events).Outcome);
    }

    [Fact]
    public void OverlappingUnsortedDeclaredGapsCannotBeHiddenByAShorterGap()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < .5 ? 0 : 1),
            Series("r", t => t < .8 ? 0 : 1), gaps: [new(.6, .61), new(.55, .9), new(.7, .71)]);
        Assert.Equal(MotionResponseOutcome.MeasurementGap, Assert.Single(result.Events).Outcome);
    }

    [Fact]
    public void AStartAtBeginningOfSelectionCannotInventRestingPrehistory()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < .01 ? 0 : 1),
            Series("r", t => t < .11 ? 0 : 1));
        Assert.Equal(MotionResponseOutcome.NoPrehistory, Assert.Single(result.Events).Outcome);
    }

    [Fact]
    public void UnequalSampleRatesReportActualResolutionAndIntervalBounds()
    {
        var command = Series("c", t => t < .5 ? 0 : 1, dt: .01);
        var response = Series("r", t => t < .615 ? 0 : 1, dt: .02);
        var result = MotionResponseAnalyzer.Analyze(command, response);

        var measured = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.Accepted, measured.Outcome);
        Assert.Equal(.02, result.SamplingResolutionSeconds, 8);
        Assert.Equal(.62, measured.ReactionTimeSeconds!.Value, 8);
        Assert.Equal(.12, measured.DelaySeconds!.Value, 8);
        Assert.Equal(.10, measured.DelayLowerBoundSeconds!.Value, 8);
        Assert.Equal(.13, measured.DelayUpperBoundSeconds!.Value, 8);
    }

    [Theory]
    [InlineData(.05)]
    [InlineData(.10)]
    public void SlowFeedbackUsesEnoughActualHistoryInsteadOfRejectingValidStableStarts(double responseDt)
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < 1 ? 0 : 1),
            Series("r", t => t < 1.2 ? 0 : 1, dt: responseDt));
        var measured = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.Accepted, measured.Outcome);
        Assert.Equal(.2, measured.DelaySeconds!.Value, 8);
        Assert.Equal(responseDt, result.SamplingResolutionSeconds, 8);
        Assert.Equal(.2 - responseDt, measured.DelayLowerBoundSeconds!.Value, 8);
    }

    [Fact]
    public void AbsoluteThresholdIsExplicitAndChangesMeaningfulCrossing()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < .5 ? 0 : 1),
            Series("r", t => Math.Clamp((t - .6) * 10, 0, 1)), new() { ResponseThresholdAbsolute = .5 });
        var measured = Assert.Single(result.Events);
        Assert.False(result.AutoThreshold);
        Assert.Equal(.5, result.ResponseThreshold);
        Assert.Equal(.65, measured.ReactionTimeSeconds!.Value, 8);
        Assert.Equal(.15, measured.DelaySeconds!.Value, 8);
    }

    [Fact]
    public void LargeManualMovementThresholdCannotHideFeedbackAlreadyMoving()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < 1 ? 0 : 1),
            Series("r", t => t), new() { ResponseThresholdAbsolute = 1 });
        Assert.Equal(MotionResponseOutcome.AlreadyMoving, Assert.Single(result.Events).Outcome);
        Assert.Null(result.AllStatistics.MeanDelaySeconds);
    }

    [Fact]
    public void TruncatedLogsNeverAcceptAnUnconfirmedReaction()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < .5 ? 0 : 1, duration: .65),
            Series("r", t => t < .65 ? 0 : 1, duration: .65));
        Assert.Equal(MotionResponseOutcome.InsufficientObservation, Assert.Single(result.Events).Outcome);
    }

    [Fact]
    public void MissingCommandCoverageCannotCertifyLaterFeedback()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < .5 ? 0 : 1, duration: .55),
            Series("r", t => t < .7 ? 0 : 1));
        Assert.Equal(MotionResponseOutcome.InsufficientObservation, Assert.Single(result.Events).Outcome);
    }

    [Fact]
    public void ConstantAndEmptySignalsReturnNoFabricatedZeroDelay()
    {
        foreach (var result in new[]
        {
            MotionResponseAnalyzer.Analyze(Series("c", _ => 2), Series("r", _ => 2)),
            MotionResponseAnalyzer.Analyze(new SignalSeries("c", [], []), new SignalSeries("r", [], []))
        })
        {
            Assert.Empty(result.Events);
            Assert.Null(result.AllStatistics.MeanDelaySeconds);
            Assert.Null(result.AllStatistics.MinimumDelaySeconds);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidAbsoluteThresholdIsRejected(double threshold)
    {
        Assert.Throws<ArgumentException>(() => MotionResponseAnalyzer.Analyze(Series("c", _ => 0), Series("r", _ => 0),
            new() { ResponseThresholdAbsolute = threshold }));
    }

    [Fact]
    public void InvalidSourceTimestampsPreservePrivacySafeTelemetryReason()
    {
        var invalid = new SignalSeries("c", [0d, .01, .01], [0d, 1, 1]);
        var error = Assert.Throws<ArgumentException>(() => MotionResponseAnalyzer.Analyze(invalid, Series("r", _ => 0)));
        Assert.Equal("duplicate_timestamp", DelayInputFailure.GetTelemetryCode(error));
    }

    [Fact]
    public void AnalysisDoesNotModifyInputSamples()
    {
        var command = Series("c", t => t < .5 ? 0 : 1);
        var response = Series("r", t => t < .6 ? 0 : 1);
        var commandValues = command.Value.ToArray(); var commandTimes = command.Time.ToArray();
        var responseValues = response.Value.ToArray(); var responseTimes = response.Time.ToArray();
        MotionResponseAnalyzer.Analyze(command, response);
        Assert.Equal(commandValues, command.Value); Assert.Equal(commandTimes, command.Time);
        Assert.Equal(responseValues, response.Value); Assert.Equal(responseTimes, response.Time);
    }

    [Fact]
    public void SlowNoisyRampDoesNotCreateRepeatedStartsAsItAccumulatesDeadbands()
    {
        static double Ramp(double t) => Math.Clamp((t - .5) * .08, 0, .4);
        var command = Series("c", t => Ramp(t) + .008 * Math.Sin(t * 170), duration: 6);
        var response = Series("r", t => Ramp(t - .15) + .008 * Math.Sin(t * 170), duration: 6);
        var result = MotionResponseAnalyzer.Analyze(command, response);

        Assert.Single(result.Events);
        var measured = result.Events[0];
        if (measured.Outcome == MotionResponseOutcome.Accepted)
        {
            Assert.True(measured.CommandTimeSeconds > .5);
            Assert.True(response.Value[measured.ReactionSourceIndex!.Value] - measured.ResponseBaseline >= result.ResponseThreshold - 1e-12);
            Assert.Equal(measured.ReactionTimeSeconds - measured.CommandTimeSeconds, measured.DelaySeconds);
            // This is threshold-to-threshold timing, not an assertion of the injected physical lag.
        }
    }

    [Fact]
    public void RepeatedSmoothSmallCorrectionsAreNotClassifiedAsSensorNoise()
    {
        static double Corrections(double t)
        {
            if (t < .5) return 0;
            if (t >= 3.5) return 20;
            var phase = (t - .5) % .3;
            return phase <= .15 ? phase * 4 : (.3 - phase) * 4;
        }
        var result = MotionResponseAnalyzer.Analyze(Series("c", Corrections, duration: 5),
            Series("r", t => Corrections(t - .04), duration: 5));

        Assert.True(result.CommandThreshold < .03);
        Assert.True(result.ResponseThreshold < .05);
        Assert.True(result.Events.Count >= 18);
        Assert.True(result.ReversalStatistics.MatchedEventCount >= 15);
    }

    [Fact]
    public void LongLogsKeepTenMillisecondSourceTimingWithoutACorrelationGrid()
    {
        var result = MotionResponseAnalyzer.Analyze(Series("c", t => t < 1000 ? 0 : .2, duration: 2000),
            Series("r", t => t < 1000.07 ? 0 : .2, duration: 2000));
        var measured = Assert.Single(result.Events);
        Assert.Equal(MotionResponseOutcome.Accepted, measured.Outcome);
        Assert.Equal(100000, measured.CommandSourceIndex);
        Assert.Equal(100007, measured.ReactionSourceIndex);
        Assert.Equal(.07, measured.DelaySeconds!.Value, 8);
        Assert.Equal(.01, result.SamplingResolutionSeconds, 8);
    }

    [Fact]
    public void StatisticsUseAcceptedEventsAndCalculateMeanMinMaxAndP95()
    {
        var result = MotionResponseAnalyzer.Analyze(
            Series("c", t => t < .5 ? 0 : t < 1.5 ? 1 : t < 2.5 ? 2 : 3),
            Series("r", t => t < .58 ? 0 : t < 1.62 ? 1 : t < 2.8 ? 2 : 3));
        Assert.Equal(3, result.AllStatistics.MatchedEventCount);
        Assert.Equal(1d / 6, result.AllStatistics.MeanDelaySeconds!.Value, 8);
        Assert.Equal(.08, result.AllStatistics.MinimumDelaySeconds!.Value, 8);
        Assert.Equal(.30, result.AllStatistics.MaximumDelaySeconds!.Value, 8);
        Assert.Equal(.282, result.AllStatistics.Percentile95DelaySeconds!.Value, 8);
    }

    private static SignalSeries Series(string name, Func<double, double> value, double dt = .01, double duration = 4)
    {
        var times = Enumerable.Range(0, (int)Math.Round(duration / dt) + 1).Select(i => Math.Round(i * dt, 9)).ToArray();
        return new SignalSeries(name, times, times.Select(value).ToArray());
    }
}
