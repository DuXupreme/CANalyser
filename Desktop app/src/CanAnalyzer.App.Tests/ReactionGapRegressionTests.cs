using System.Reflection;
using System.Text.Json;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ReactionGapRegressionTests
{
    [Theory]
    [InlineData("legacy", true)]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("unrelated", false)]
    public Task OptionalCorrelationCannotBridgeRelevantImportGaps(string channel, bool blocked) =>
        PlotRegressionTests.OnDispatcher(async () =>
        {
            using var source = ReactionAnalysisRegressionTests.RampDataset();
            var original = source.SignalSeriesByLabel;
            SignalSeries[] signals = [OnChannel(original["command"], "1"), OnChannel(original["feedback"], "2")];
            var report = new ImportReport("synthetic", 0, 0, 0, 0, [])
            {
                Gaps = channel == "legacy" ? [new(4.02, 4.3)] : [],
                GapsByChannel = channel == "legacy" ? new Dictionary<string, IReadOnlyList<MeasurementGap>>() :
                    new Dictionary<string, IReadOnlyList<MeasurementGap>> { [channel] = [new(4.02, 4.3)] }
            };
            using var data = new CanDataset
            {
                RawFrames = [], DecodedSamples = [], MessageSummaries = [],
                SignalLabels = signals.Select(signal => signal.Label).ToArray(),
                SignalSeriesByLabel = signals.ToDictionary(signal => signal.Label),
                SignalSeriesByIdentity = signals.ToDictionary(signal => signal.Identity),
                Diagnostics = new(0, 0, 2, 0, 0, ""), ImportReport = report
            };
            var telemetry = new AnalyticsRegressionTests.Telemetry();
            var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
            vm.LoadDataset(data); await ReactionAnalysisRegressionTests.Wait(vm);
            vm.PositionReferenceConfirmed = true;
            vm.CalculateCorrelation = true;
            await vm.RecomputeCommand.ExecuteAsync(null);

            var delay = JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Delay");
            Assert.Equal(blocked ? JsonValueKind.Null : JsonValueKind.Object, delay.GetProperty("Correlation").ValueKind);
            Assert.Contains(vm.ReactionEvents, entry => entry.Accepted && entry.Measurement.CommandTimeSeconds < 2);
            if (blocked)
            {
                Assert.Contains(vm.DelayMetrics, metric => metric.Name == "Correlatie" && metric.Value.Contains("meetgat"));
                Assert.DoesNotContain(vm.ReactionEvents, entry => entry.Accepted && entry.Measurement.CommandTimeSeconds > 4);
                AssertRawPreview(vm.DelayOverlayModel);
            }
            else Assert.Contains(vm.ReactionEvents, entry => entry.Accepted && entry.Measurement.CommandTimeSeconds > 4);
            Assert.False(vm.IsBusy);
        });

    [Theory]
    [InlineData(1001)]
    [InlineData(100001)]
    public Task NoDetectedActionsStillShowSourceSignalsWithoutInventingDelay(int sampleCount) =>
        PlotRegressionTests.OnDispatcher(async () =>
        {
            var time = Enumerable.Range(0, sampleCount).Select(i => i * 10d / (sampleCount - 1)).ToArray();
            SignalSeries[] signals = [new("command", time, time.Select(_ => 10d).ToArray()),
                new("feedback", time, time.Select(_ => 9d).ToArray())];
            using var data = new CanDataset
            {
                RawFrames = [], DecodedSamples = [], MessageSummaries = [],
                SignalLabels = signals.Select(s => s.Label).ToArray(), SignalSeriesByLabel = signals.ToDictionary(s => s.Label),
                SignalSeriesByIdentity = signals.ToDictionary(s => s.Identity), Diagnostics = new(0, 0, 2, 0, 0, ""),
                ImportReport = new("synthetic", 0, 0, 0, 0, []) { Gaps = [new(4.02, 4.3)] }
            };
            var telemetry = new AnalyticsRegressionTests.Telemetry();
            var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
            vm.LoadDataset(data); await ReactionAnalysisRegressionTests.Wait(vm);
            vm.PositionReferenceConfirmed = true;
            await vm.RecomputeCommand.ExecuteAsync(null);
            Assert.Empty(vm.ReactionEvents);
            Assert.Equal("—", vm.ReactionMeanText);
            AssertRawPreview(vm.ReactionEventPlot);
            AssertRawPreview(vm.DelayOverlayModel);
            Assert.All(vm.ReactionEventPlot.Series, series => Assert.InRange(((LineSeries)series).Points.Count, 2, 8000));
            Assert.True(vm.ShowReactionSourceGapCommand.CanExecute(null));
            Assert.Contains("1 meetgat", vm.ReactionGapSummary);
            vm.ShowReactionSourceGapCommand.Execute(null);
            Assert.Contains("4,02", vm.ReactionDetailText.Replace('.', ','));
            Assert.False(vm.UseJoystickTimeWindow); // Inspecting the gap never silently changes the numerical scope.
            Assert.Equal(sampleCount, signals[0].Time.Length);
            Assert.DoesNotContain("analytics_recompute_failed", telemetry.Events);
            await vm.CancelAndWaitAsync();
        });

    private static void AssertRawPreview(PlotModel model)
    {
        Assert.Equal(2, model.Series.Count);
        foreach (var series in model.Series)
        {
            var line = Assert.IsAssignableFrom<LineSeries>(series);
            Assert.Contains(line.Points, point => double.IsFinite(point.X) && point.X < 4);
            Assert.Contains(line.Points, point => double.IsFinite(point.X) && point.X > 4.3);
        }
        Assert.Contains(model.Series.OfType<LineSeries>().SelectMany(line => line.Points), point => double.IsNaN(point.X));
        Assert.Contains(model.Annotations, item => item is OxyPlot.Annotations.RectangleAnnotation);
    }

    [Fact]
    public Task AllActionsExcludedByGapsKeepAuditableRowsAndNoDelayNumber() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var source = ReactionAnalysisRegressionTests.RampDataset();
        using var data = new CanDataset
        {
            RawFrames = [], DecodedSamples = [], MessageSummaries = [], SignalLabels = source.SignalLabels,
            SignalSeriesByLabel = source.SignalSeriesByLabel, SignalSeriesByIdentity = source.SignalSeriesByIdentity,
            Diagnostics = source.Diagnostics, ImportReport = new("synthetic", 0, 0, 0, 0, []) { Gaps = [new(.8, 1.5), new(3.8, 4.5)] }
        };
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
        vm.LoadDataset(data); await ReactionAnalysisRegressionTests.Wait(vm);
        vm.SelectedReactionReference = vm.ReactionReferences[0];
        Assert.NotEmpty(vm.ReactionEvents);
        Assert.All(vm.ReactionEvents, row => Assert.Equal(MotionResponseOutcome.MeasurementGap, row.Measurement.Outcome));
        Assert.Equal("—", vm.ReactionMeanText);
        Assert.Contains("uitgesloten", vm.ReactionGapSummary);
        Assert.Equal(2, vm.ReactionEventPlot.Series.Count);
        Assert.Equal(2, vm.DelayOverlayModel.Series.Count);
        Assert.DoesNotContain("analytics_recompute_failed", telemetry.Events);
        await vm.CancelAndWaitAsync();
    });

    [Fact]
    public Task FirstNarrowGapInLongLogUsesFreshLocalSourceDetail() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var time = Enumerable.Range(0, 100001).Select(i => i / 100d).ToArray();
        SignalSeries[] signals = [new("command", time, time.Select(_ => 10d).ToArray()),
            new("feedback", time, time.Select(_ => 9d).ToArray())];
        using var data = new CanDataset
        {
            RawFrames = [], DecodedSamples = [], MessageSummaries = [], SignalLabels = signals.Select(s => s.Label).ToArray(),
            SignalSeriesByLabel = signals.ToDictionary(s => s.Label), SignalSeriesByIdentity = signals.ToDictionary(s => s.Identity),
            Diagnostics = new(0, 0, 2, 0, 0, ""),
            ImportReport = new("synthetic", 0, 0, 0, 0, []) { Gaps = [new(500.124, 500.126)] }
        };
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
        vm.LoadDataset(data); await ReactionAnalysisRegressionTests.Wait(vm);
        Assert.DoesNotContain(vm.DelayOverlayModel.Series.OfType<LineSeries>().SelectMany(s => s.Points), p => p.X == 500.12);
        vm.ShowReactionSourceGapCommand.Execute(null);
        foreach (var line in vm.ReactionEventPlot.Series.OfType<LineSeries>())
        {
            Assert.Contains(line.Points, p => p.X == 500.12);
            Assert.Contains(line.Points, p => p.X == 500.13);
            Assert.Contains(line.Points, p => double.IsNaN(p.X));
            Assert.InRange(line.Points.Count(p => double.IsFinite(p.X)), 35, 50);
            Assert.All(line.Points.Where(p => double.IsFinite(p.X)), p => Assert.InRange(p.X, 499.91, 500.34));
        }
        Assert.Equal("—", vm.ReactionMeanText);
        Assert.False(vm.UseJoystickTimeWindow);
        await vm.CancelAndWaitAsync();
    });

    [Fact]
    public Task ValidActionsBeforeAndAfterGapRemainMeasured() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var time = Enumerable.Range(0, 1001).Select(i => i / 100d).ToArray();
        static double Value(double t) => t < 1 ? 0 : t < 4 ? 1 : t < 7 ? 0 : 1;
        SignalSeries[] signals = [new("command", time, time.Select(Value).ToArray()),
            new("feedback", time, time.Select(t => Value(t - .12)).ToArray())];
        using var data = new CanDataset
        {
            RawFrames = [], DecodedSamples = [], MessageSummaries = [], SignalLabels = signals.Select(s => s.Label).ToArray(),
            SignalSeriesByLabel = signals.ToDictionary(s => s.Label), SignalSeriesByIdentity = signals.ToDictionary(s => s.Identity),
            Diagnostics = new(0, 0, 2, 0, 0, ""), ImportReport = new("synthetic", 0, 0, 0, 0, []) { Gaps = [new(3.95, 4.3)] }
        };
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
        vm.LoadDataset(data); await ReactionAnalysisRegressionTests.Wait(vm);
        vm.SelectedReactionReference = vm.ReactionReferences[0];
        Assert.Contains(vm.ReactionEvents, e => e.Accepted && e.Measurement.CommandTimeSeconds < 2);
        Assert.Contains(vm.ReactionEvents, e => e.Accepted && e.Measurement.CommandTimeSeconds > 6);
        Assert.Contains(vm.ReactionEvents, e => e.Measurement.Outcome == MotionResponseOutcome.MeasurementGap);
        Assert.DoesNotContain("analytics_recompute_failed", telemetry.Events);
        AssertRawPreview(vm.DelayOverlayModel);
        await vm.CancelAndWaitAsync();
    });

    [Fact]
    public void EventPreviewPreservesSamplesAndBreaksWithoutScanningAllGapsPerPoint()
    {
        var time = Enumerable.Range(0, 10_001).Select(index => index / 1000d).ToArray();
        var source = new SignalSeries("source", time, time.Select(value => value * 2).ToArray());
        MeasurementGap[] intervals = [new(7, 8), new(2, 4), new(1, 5), new(double.NaN, 3),
            .. Enumerable.Range(0, 1_000).Select(index => new MeasurementGap(100 + index * 2, 101 + index * 2))];
        var gaps = new CountingGaps(intervals);
        var method = typeof(JoystickAnalyticsViewModel).GetMethod("EventSeries", BindingFlags.NonPublic | BindingFlags.Static)!;
        var first = (StairStepSeries)method.Invoke(null, [source, "source", OxyColors.Blue, 0d, 10d, 1d, gaps])!;
        var second = (StairStepSeries)method.Invoke(null, [source, "source", OxyColors.Blue, 0d, 10d, 1d, gaps])!;
        var expected = new List<DataPoint>();
        for (var index = 0; index < time.Length; index++)
        {
            if (index > 0 && intervals.Any(gap => gap.StartSeconds < time[index] && gap.EndSeconds > time[index - 1]))
                expected.Add(DataPoint.Undefined);
            expected.Add(new(time[index], source.Value[index]));
        }
        Assert.Equal(expected.Select(point => point.X), first.Points.Select(point => point.X));
        Assert.Equal(expected.Select(point => point.Y), first.Points.Select(point => point.Y));
        Assert.Equal(expected.Select(point => point.X), second.Points.Select(point => point.X));
        Assert.Equal(expected.Select(point => point.Y), second.Points.Select(point => point.Y));
        Assert.Equal(1, gaps.EnumerationCount);
    }

    [Fact]
    public Task TrackingPreviewIndexesGapsAndKeepsUnknownIntervalsDisconnected() =>
        PlotRegressionTests.OnDispatcher(async () =>
        {
            using var source = ReactionAnalysisRegressionTests.RampDataset();
            var telemetry = new AnalyticsRegressionTests.Telemetry();
            var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
            vm.LoadDataset(source); await ReactionAnalysisRegressionTests.Wait(vm);
            var gaps = new CountingGaps([new(4.02, 4.3),
                .. Enumerable.Range(0, 1_000).Select(index => new MeasurementGap(100 + index * 2, 101 + index * 2))]);
            using var data = new CanDataset
            {
                RawFrames = [], DecodedSamples = [], MessageSummaries = [],
                SignalLabels = source.SignalLabels, SignalSeriesByLabel = source.SignalSeriesByLabel,
                SignalSeriesByIdentity = source.SignalSeriesByIdentity, Diagnostics = source.Diagnostics,
                ImportReport = new("synthetic", 0, 0, 0, 0, []) { Gaps = gaps }
            };
            typeof(JoystickAnalyticsViewModel).GetField("_dataset", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, data);
            var method = typeof(JoystickAnalyticsViewModel).GetMethod("BuildReactionPlot", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var plot = (PlotModel)method.Invoke(vm, [0d, 10d, null, true])!;
                var line = Assert.IsType<StairStepSeries>(Assert.Single(plot.Series));
                Assert.Contains(line.Points, point => double.IsFinite(point.X) && point.X < 4);
                Assert.Contains(line.Points, point => double.IsNaN(point.X));
                Assert.DoesNotContain(line.Points, point => point.X > 4.02 && point.X < 4.3);
                Assert.Contains(line.Points, point => double.IsFinite(point.X) && point.X > 4.3);
            }
            // One index build plus one annotation pass per preview; never one scan per point.
            Assert.InRange(gaps.EnumerationCount, 1, 3);
        });

    private static SignalSeries OnChannel(SignalSeries source, string channel) => new(
        new SignalIdentity(channel, CanFrameFormat.Classic, false, 1, source.Label, "value"),
        source.TimestampNanoseconds, source.Value, source.Label);

    private sealed class CountingGaps(MeasurementGap[] gaps) : IReadOnlyList<MeasurementGap>
    {
        public int EnumerationCount { get; private set; }
        public int Count => gaps.Length;
        public MeasurementGap this[int index] => gaps[index];
        public IEnumerator<MeasurementGap> GetEnumerator()
        {
            EnumerationCount++;
            return ((IEnumerable<MeasurementGap>)gaps).GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
