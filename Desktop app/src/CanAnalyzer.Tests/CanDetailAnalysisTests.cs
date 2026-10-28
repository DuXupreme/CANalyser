using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class CanDetailAnalysisTests
{
    [Fact]
    public void ExactSourceOrderRetainsTimingFaultsWithoutPollutingPositiveCycleStatistics()
    {
        using var dataset = Dataset([Frame(0), Frame(.1), Frame(.3), Frame(.2), Frame(.2), Frame(.5),
            Frame(.1) with { Direction = CanFrameDirection.Transmit },
            Frame(.2) with { FrameFormat = CanFrameFormat.FlexibleDataRate }, Frame(.3) with { IsExtended = true }]);
        var result = Analyze(dataset);
        Assert.Equal(9, result.Value!.FrameCount);
        Assert.Equal(4, result.Value.Streams.Count);
        var stream = Assert.Single(result.Value.Streams, item => item.Count == 6);
        Assert.Equal(3, stream.CycleSamples);
        Assert.Equal(1, stream.NegativeCycleCount);
        Assert.Equal(2, stream.NonIncreasingCycleCount);
        Assert.Equal(200, stream.AverageCycleMs!.Value, 8);
        Assert.Equal(100, stream.JitterMs!.Value, 8);
        Assert.Equal(100, stream.MinCycleMs!.Value, 8);
        Assert.Equal(300, stream.MaxCycleMs!.Value, 8);
        Assert.Equal(8, stream.MeanPayloadLength);
    }

    [Fact]
    public void WindowAndInternalDiagnosticExclusionMatchStandardPlan()
    {
        var frames = new[] { Frame(-10, "internal"), Frame(0), Frame(1), Frame(2), Frame(3), Frame(100, "internal"), Frame(1, "other") };
        using var dataset = Dataset(frames, Report(frames) with { InternalDiagnosticChannels = ["internal"] });
        var service = new AnalysisRunService();
        var request = new AnalysisRunRequest { Scope = "TimeWindow", StartSeconds = 1, EndSeconds = 2, Modules = AnalysisModules.Can };
        var standard = Assert.Single(service.Analyze(dataset, request).CanChannels, item => item.SubjectId == "1");
        var detail = service.AnalyzeCanDetail(dataset, request, "1");
        Assert.Equal(2, detail.Value!.FrameCount);
        Assert.Equal(1, detail.Value.StartSeconds);
        Assert.Equal(2, detail.Value.EndSeconds);
        Assert.Equal(standard.Coverage, detail.Coverage);
        Assert.Equal(standard.Status, detail.Status);
        Assert.Equal(standard.Value!.FramesPerSecond, detail.Value.AverageFramesPerSecond);
        var excluded = service.AnalyzeCanDetail(dataset, new(), "internal");
        Assert.Null(excluded.Value);
        Assert.Contains("internal_diagnostic_channel", excluded.ReasonCodes);
        Assert.Equal(3, excluded.Coverage.RequestedSeconds);
    }

    [Fact]
    public void MissingIntervalsAreNotTrafficZerosOrCycleSamples()
    {
        var frames = new[] { Frame(0), Frame(1), Frame(2), Frame(3) };
        using var dataset = Dataset(frames, Report(frames) with { Gaps = [new(1, 2)] });
        var result = Analyze(dataset, confirmed: true);
        Assert.Equal(2, result.Coverage.ObservedSeconds);
        Assert.Equal(2, result.Value!.AverageFramesPerSecond);
        Assert.Equal(4, result.Value.Bins.Sum(bin => bin.FrameCount));
        var unknown = Assert.Single(result.Value.Bins, bin => bin.StartSeconds == 1);
        Assert.Equal(0, unknown.CoveredSeconds);
        Assert.Null(unknown.FramesPerSecond);
        Assert.Null(unknown.BusLoadPercent);
        var stream = Assert.Single(result.Value.Streams);
        Assert.Equal(2, stream.CycleSamples);
        Assert.Equal(1, stream.ExcludedGapCycles);
        Assert.Equal(1000, stream.AverageCycleMs);
    }

    [Fact]
    public void BusLoadRequiresConfirmedRatesAndUsesSameClassicFdModel()
    {
        using var dataset = Dataset([Frame(0), Frame(.5) with { FrameFormat = CanFrameFormat.FlexibleDataRate, BitRateSwitch = true }, Frame(1)]);
        var unknown = Analyze(dataset);
        Assert.Contains("bitrates_unconfirmed", unknown.ReasonCodes);
        Assert.Null(unknown.Value!.AverageBusLoadPercent);
        Assert.Null(unknown.Value.EstimatedTransmissionSeconds);
        Assert.All(unknown.Value.Bins, bin => Assert.Null(bin.BusLoadPercent));
        var known = Analyze(dataset, confirmed: true);
        var classic = (47d + 64 + 3) * 1.20 / 500_000;
        var fd = 23d * 1.20 / 500_000 + (64 + 17 + 13) * 1.20 / 2_000_000;
        Assert.Equal(2 * classic + fd, known.Value!.EstimatedTransmissionSeconds!.Value, 12);
        Assert.Equal(100 * (2 * classic + fd), known.Value.AverageBusLoadPercent!.Value, 10);
        Assert.NotEqual(unknown.ResultId, known.ResultId);
    }

    [Fact]
    public void DetailIsCancellableBoundedAndHasStableParameterDependentIdentity()
    {
        using var dataset = Dataset([Frame(0), Frame(1_000_000)]);
        var service = new AnalysisRunService();
        var first = service.AnalyzeCanDetail(dataset, new(), "1", .05);
        Assert.Equal(200_000, first.Value!.Bins.Count);
        Assert.True(first.Value.ActualBinWidthSeconds > .05);
        Assert.Equal(first.ResultId, service.AnalyzeCanDetail(dataset, new(), "1", .05).ResultId);
        Assert.NotEqual(first.ResultId, service.AnalyzeCanDetail(dataset, new(), "1", 1).ResultId);
        Assert.Equal(ModuleStatus.NotRequested, service.AnalyzeCanDetail(dataset, new() { Modules = AnalysisModules.Joystick }, "1").Status);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.AnalyzeCanDetail(dataset, new(), "1", cancellationToken: cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.AnalyzeCanDetail(dataset, new(), "1", double.NaN));
    }

    [Fact]
    public void NanosecondCyclesAreNotLostAtLargeLoggerOffsets()
    {
        var frame = Frame(1_000_000_000);
        using var dataset = Dataset([frame, frame with { TimestampNanoseconds = frame.TimestampNanoseconds + 1 }]);
        var stream = Assert.Single(Analyze(dataset).Value!.Streams);
        Assert.Equal(1, stream.CycleSamples);
        Assert.Equal(.000001, stream.AverageCycleMs!.Value, 12);
        Assert.Equal(0, stream.NonIncreasingCycleCount);
    }

    private static ModuleResult<CanDetailResult> Analyze(CanDataset dataset, bool confirmed = false) =>
        new AnalysisRunService().AnalyzeCanDetail(dataset, new()
        {
            Profile = AnalysisProfileResolver.Recognize(dataset) with { CanBitratesConfirmed = confirmed }
        }, "1");
    private static ImportReport Report(RawCanFrame[] frames) => new("fixture", frames.Length, 0, frames.Length, 0, []);
    private static RawCanFrame Frame(double time, string channel = "1") =>
        new(checked((long)Math.Round(time * 1_000_000_000d)), 1, 8, new byte[8], "Rx", channel, false,
            Direction: CanFrameDirection.Receive);
    private static CanDataset Dataset(RawCanFrame[] frames, ImportReport? report = null) => new()
    {
        RawFrames = frames, DecodedSamples = [], MessageSummaries = [], SignalLabels = [],
        SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>(), SignalSeriesByLabel = new Dictionary<string, SignalSeries>(),
        Diagnostics = new(0, 0, 0, 0, 0, ""), ImportReport = report, SourceLogSha256 = new string('a', 64), DbcSha256 = new string('b', 64)
    };
}
