using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using OxyPlot.Annotations;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ReactionAnalysisRegressionTests
{
    internal static CanDataset RampDataset()
    {
        var time = Enumerable.Range(0, 1001).Select(i => i / 100d).ToArray();
        static double Value(double t) => t < 1 ? 100 : t < 2 ? 100 + .02 * (t - 1) : t < 4 ? 100.02 : t < 5 ? 100.02 - .01 * (t - 4) : 100.01;
        SignalSeries[] series = [new("command", time, time.Select(Value).ToArray()),
            new("feedback", time, time.Select(t => Value(t - .12)).ToArray())];
        return new() { RawFrames = [], DecodedSamples = [], MessageSummaries = [],
            SignalLabels = series.Select(s => s.Label).ToArray(), SignalSeriesByLabel = series.ToDictionary(s => s.Label),
            SignalSeriesByIdentity = series.ToDictionary(s => s.Identity), Diagnostics = new(0, 0, 2, 0, 0, "") };
    }

    private static JoystickAnalyticsViewModel Model()
    {
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        return new(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
    }

    internal static async Task Wait(JoystickAnalyticsViewModel vm)
    {
        var timer = Stopwatch.StartNew();
        while (vm.IsBusy) { Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15)); await Task.Delay(5); }
    }

    private static async Task ConfirmReferenceAndWait(JoystickAnalyticsViewModel vm)
    {
        await Wait(vm);
        vm.PositionReferenceConfirmed = true;
        vm.SelectedReactionReference = vm.ReactionReferences[1];
        await vm.RecomputeCommand.ExecuteAsync(null);
    }

    [Fact]
    public Task GradualSmallChangesProduceAuditableMillisecondsWithoutCorrelation() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = RampDataset();
        var vm = Model(); vm.LoadDataset(data); await ConfirmReferenceAndWait(vm);
        Assert.False(vm.CalculateCorrelation);
        var accepted = vm.ReactionEvents.Where(e => e.Accepted).ToArray();
        Assert.True(accepted.Length >= 2, vm.ReactionStatus);
        Assert.All(accepted, e => Assert.InRange(e.Measurement.DelaySeconds!.Value, .10, .15));
        Assert.Contains("ms", vm.ReactionMeanText);
        Assert.Single(vm.ReactionEventPlot.Legends);
        Assert.Equal(2, vm.ReactionEventPlot.Series.Count);
        var selected = vm.SelectedReactionEvent!.Measurement;
        var markers = vm.ReactionEventPlot.Annotations.OfType<LineAnnotation>().Where(a => a.Type == LineAnnotationType.Vertical).ToArray();
        Assert.Contains(markers, a => a.X == selected.CommandTimeSeconds && a.Text == "Positievraag");
        Assert.Contains(markers, a => a.X == selected.ReactionTimeSeconds && a.Text == "Reactie");
        Assert.All(vm.ReactionEventPlot.Series.OfType<LineSeries>(), s => Assert.NotEmpty(s.Points));
        var export = JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Delay");
        Assert.Equal("source-sample-dual-reference-v2", export.GetProperty("Method").GetString());
        Assert.Equal(2, vm.ReactionReferenceSummaries.Count);
        Assert.All(vm.ReactionReferenceSummaries, summary => Assert.NotEqual("—", summary.Mean));
        Assert.Equal("PositionRequest", export.GetProperty("PositionRequest").GetProperty("Reference").GetString());
        Assert.NotEmpty(export.GetProperty("PositionRequest").GetProperty("Result").GetProperty("Events").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, export.GetProperty("Correlation").ValueKind);
        Assert.NotEmpty(export.GetProperty("MotionResponse").GetProperty("Events").EnumerateArray());
        vm.InspectReactionMetricCommand.Execute("max");
        Assert.Equal(accepted.Max(e => e.Measurement.DelaySeconds), vm.SelectedReactionEvent!.Measurement.DelaySeconds);
    });

    [Fact]
    public Task ChangedSettingsAndEmptyCategoriesCannotShowPreviousMeasurements() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = RampDataset(); var vm = Model(); vm.LoadDataset(data); await ConfirmReferenceAndWait(vm);
        Assert.NotEqual("—", vm.ReactionMeanText);
        vm.SelectedReactionKind = vm.ReactionKinds[1];
        if (vm.ReactionEvents.Count == 0)
        {
            Assert.Equal(2, vm.ReactionEventPlot.Series.Count);
            Assert.Contains("Bronpreview", vm.ReactionEventPlot.Title);
            Assert.Empty(vm.ReactionEventPlot.Annotations.OfType<OxyPlot.Annotations.LineAnnotation>());
            Assert.Empty(vm.MotionTrackingPlot.Series);
            Assert.Equal("—", vm.ReactionMeanText);
        }
        vm.ResponseThresholdAbsolute = .03; vm.UseAutomaticResponseThreshold = false;
        Assert.Empty(vm.ReactionEventPlot.Series); // Dirty settings cannot retain a preview from the previous calculation.
        Assert.Empty(vm.ReactionEvents); Assert.Equal("—", vm.ReactionMeanText);
        Assert.All(vm.ReactionReferenceSummaries, summary => Assert.Equal("—", summary.Mean));
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Delay").GetProperty("PositionRequest").GetProperty("Result").ValueKind);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Delay").GetProperty("MotionResponse").ValueKind);
        await vm.RecomputeCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.ReactionEvents, e => e.Accepted);
    });

    [Fact]
    public Task TwoReferencesShowDifferentOnsetsAndKeepIndependentExports() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var times = Enumerable.Range(0, 401).Select(i => i / 100d).ToArray();
        SignalSeries[] series = [new("command", times, times.Select(t => t <= 1 ? 140 : t < 1.6 ? 140 - 100 * (t - 1) : 80).ToArray()),
            new("feedback", times, times.Select(t => t < 1.5 ? 100d : 98d).ToArray())];
        using var data = new CanDataset { RawFrames = [], DecodedSamples = [], MessageSummaries = [],
            SignalLabels = series.Select(s => s.Label).ToArray(), SignalSeriesByLabel = series.ToDictionary(s => s.Label),
            SignalSeriesByIdentity = series.ToDictionary(s => s.Identity), Diagnostics = new(0, 0, 2, 0, 0, "") };
        var vm = Model(); vm.LoadDataset(data); await ConfirmReferenceAndWait(vm);
        var export = JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Delay");
        static JsonElement Accepted(JsonElement delay, string name) => delay.GetProperty(name).GetProperty("Result")
            .GetProperty("Events").EnumerateArray().Single(e => e.GetProperty("Outcome").GetInt32() == (int)MotionResponseOutcome.Accepted);
        var descriptive = Accepted(export, "SetpointChange");
        var positional = Accepted(export, "PositionRequest");
        Assert.True(positional.GetProperty("CommandTimeSeconds").GetDouble() > descriptive.GetProperty("CommandTimeSeconds").GetDouble() + .3);
        Assert.Equal(descriptive.GetProperty("ReactionTimeSeconds").GetDouble(), positional.GetProperty("ReactionTimeSeconds").GetDouble());
        Assert.True(positional.GetProperty("DelaySeconds").GetDouble() < descriptive.GetProperty("DelaySeconds").GetDouble());
        vm.SelectedReactionKind = positional.GetProperty("Kind").GetInt32() == (int)MotionResponseKind.Start ? vm.ReactionKinds[0] : vm.ReactionKinds[1];
        vm.InspectReactionReferenceCommand.Execute(vm.ReactionReferences[1]);
        Assert.Contains("Positievraag", vm.ReactionEventPlot.Title);
        Assert.Equal(3, vm.ReactionEventPlot.Annotations.OfType<LineAnnotation>().Count(a => a.Type == LineAnnotationType.Vertical));
        Assert.Contains("later", vm.ReactionDetailText);
        vm.InspectReactionReferenceCommand.Execute(vm.ReactionReferences[0]);
        vm.SelectedReactionKind = descriptive.GetProperty("Kind").GetInt32() == (int)MotionResponseKind.Start ? vm.ReactionKinds[0] : vm.ReactionKinds[1];
        Assert.Contains("Setpointwijziging", vm.ReactionEventPlot.Title);
        Assert.Equal(2, vm.ReactionEventPlot.Annotations.OfType<LineAnnotation>().Count(a => a.Type == LineAnnotationType.Vertical));
        Assert.Equal(export.GetRawText(), JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Delay").GetRawText());
        vm.PositionToleranceAbsolute = 2;
        Assert.All(vm.ReactionReferenceSummaries, summary => Assert.Equal("—", summary.Mean));
        Assert.Empty(vm.ReactionEvents);
    });

    [Fact]
    public Task ExcludedSetpointChangeIsNotMarkedAsAnActualPositionRequest() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var times = Enumerable.Range(0, 301).Select(i => i / 100d).ToArray();
        SignalSeries[] series = [new("command", times, times.Select(t => t < 1 ? 2d : 1d).ToArray()),
            new("feedback", times, new double[times.Length])];
        using var data = new CanDataset { RawFrames = [], DecodedSamples = [], MessageSummaries = [],
            SignalLabels = series.Select(s => s.Label).ToArray(), SignalSeriesByLabel = series.ToDictionary(s => s.Label),
            SignalSeriesByIdentity = series.ToDictionary(s => s.Identity), Diagnostics = new(0, 0, 2, 0, 0, "") };
        var vm = Model(); vm.LoadDataset(data); await ConfirmReferenceAndWait(vm);
        vm.SelectedReactionKind = vm.ReactionKinds[1];
        var row = Assert.Single(vm.ReactionEvents);
        Assert.Equal(MotionResponseOutcome.NoNewDirectionRequest, row.Measurement.Outcome);
        Assert.Equal("—", vm.ReactionMeanText);
        var marker = Assert.Single(vm.ReactionEventPlot.Annotations.OfType<LineAnnotation>(), a => a.Type == LineAnnotationType.Vertical);
        Assert.Contains("geen bevestigde vraag", marker.Text);
    });

    [Fact]
    public Task ImportGapExcludesAffectedEventsButPreservesUnrelatedMeasurements() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var source = RampDataset();
        using var data = new CanDataset { RawFrames = [], DecodedSamples = [], MessageSummaries = [],
            SignalLabels = source.SignalLabels, SignalSeriesByLabel = source.SignalSeriesByLabel,
            SignalSeriesByIdentity = source.SignalSeriesByIdentity, Diagnostics = source.Diagnostics,
            Completeness = DatasetCompleteness.Partial,
            ImportReport = new("synthetic", 0, 0, 0, 0, []) { Gaps = [new(4.02, 4.3)] } };
        var vm = Model(); vm.LoadDataset(data); await ConfirmReferenceAndWait(vm);
        Assert.Contains(vm.ReactionEvents, e => e.Accepted && e.Measurement.CommandTimeSeconds < 2);
        Assert.DoesNotContain(vm.ReactionEvents, e => e.Accepted && e.Measurement.CommandTimeSeconds > 4);
        Assert.Contains(vm.ReactionEvents, e => e.Measurement.Outcome == MotionResponseOutcome.MeasurementGap);
        Assert.Contains("PARTIAL", vm.StatusText);
    });

    [Fact]
    public Task LeavingNamedActuatorInvalidatesThePreviouslyFilteredTrackingResult() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = RampDataset(); var vm = Model(); vm.LoadDataset(data); await ConfirmReferenceAndWait(vm);
        vm.SelectedActuatorLeftSignal = "feedback";
        vm.ActuatorRanges[0].SetpointSignal = "command";
        vm.ActuatorRanges[0].RunSignal = "command";
        vm.ActuatorRanges[0].RunValue = 100;
        vm.SelectedReactionActuator = "Links";
        vm.PositionReferenceConfirmed = true;
        await vm.RecomputeCommand.ExecuteAsync(null);
        Assert.Equal(JsonValueKind.Object, JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Delay").GetProperty("MotionTracking").ValueKind);
        vm.SelectedReactionActuator = "Vrije signaalkeuze";
        Assert.Equal("—", vm.MotionTrackingMeanText);
        Assert.Empty(vm.ReactionEvents);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Delay").GetProperty("MotionTracking").ValueKind);
    });

    [Fact]
    public Task JoystickPhysicalSummaryIsIndependentOfActuatorCalibrationAndCanBeInspected() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var source = RampDataset();
        var time = source.SignalSeriesByLabel["command"].Time;
        var x = new SignalSeries(new SignalIdentity("1", CanFrameFormat.Classic, false, 0x301, "joystick", "pos_x"),
            time.Select(t => (long)(t * 1_000_000_000)).ToArray(),
            time.Select(t => t is >= .6 and < .9 ? .2 : t is >= 2 and < 5 ? 1 : t is >= 6 and < 7.5 ? .5 : 0).ToArray());
        var y = new SignalSeries(new SignalIdentity("1", CanFrameFormat.Classic, false, 0x301, "joystick", "pos_y"),
            time.Select(t => (long)(t * 1_000_000_000)).ToArray(), new double[time.Length]);
        var all = source.SignalSeriesByLabel.Values.Concat([x, y]).ToArray();
        using var data = new CanDataset { RawFrames = [], DecodedSamples = [], MessageSummaries = [],
            SignalLabels = all.Select(s => s.Label).ToArray(), SignalSeriesByLabel = all.ToDictionary(s => s.Label),
            SignalSeriesByIdentity = all.ToDictionary(s => s.Identity), Diagnostics = new(0, 0, 4, 0, 0, "") };
        var vm = Model(); vm.LoadDataset(data); await Wait(vm);
        Assert.Empty(vm.JoystickAmplitudeCards);
        vm.UseJoystickCalibration = true;
        await vm.RecomputeUsageCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.JoystickAmplitudeCards.Count);
        Assert.Equal("1", vm.JoystickCorrectionCountText);
        Assert.Equal("1", vm.JoystickFullHoldCountText);
        Assert.Single(vm.JoystickInputPreviewModel.Legends);
        Assert.Equal(2, vm.JoystickInputPreviewModel.Series.Count);
        Assert.All(vm.ActuatorRanges, range => Assert.Null(range.ManualCenter));
        var target = Environment.GetEnvironmentVariable("CANALYSER_ANALYSIS_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(target))
        {
            var view = new JoystickUsageView { DataContext = vm, Width = 1400, Height = 1000 };
            view.Measure(new Size(1400, 1000)); view.Arrange(new Rect(0, 0, 1400, 1000)); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1400, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(Path.GetDirectoryName(target)!, "joystick-view.png")); png.Save(file);
        }
        vm.JoystickXMaximum = .5;
        Assert.Empty(vm.JoystickAmplitudeCards);
        Assert.Empty(vm.JoystickInputRows);
    });

    [Fact]
    public Task CombinedViewRendersAndBindingsResolve() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = RampDataset(); var vm = Model(); vm.LoadDataset(data); await Wait(vm);
        using var errors = new StringWriter();
        using var listener = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            var view = new JoystickAnalyticsView { DataContext = vm, Width = 1400, Height = 1000 };
            view.Measure(new Size(1400, 1000)); view.Arrange(new Rect(0, 0, 1400, 1000)); view.UpdateLayout();
            var tabs = Find<TabControl>(view).Single();
            Assert.Equal(5, tabs.Items.Count);
            Assert.Equal("Overzicht", ((TabItem)tabs.Items[0]).Header);
            Assert.Equal("Reactietijd & actuatorvolging", ((TabItem)tabs.Items[1]).Header);
            foreach (TabItem tab in tabs.Items)
            {
                tab.IsSelected = true; view.UpdateLayout();
                await System.Windows.Threading.Dispatcher.Yield();
            }
            tabs.SelectedIndex = 0; view.UpdateLayout();
            var target = Environment.GetEnvironmentVariable("CANALYSER_ANALYSIS_SCREENSHOT");
            if (!string.IsNullOrWhiteSpace(target))
            {
                var bitmap = new RenderTargetBitmap(1400, 1000, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(target); png.Save(file);
            }
            listener.Flush();
            Assert.DoesNotContain("BindingExpression path error", errors.ToString());
            Assert.DoesNotContain("Cannot find source", errors.ToString());
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(listener); }
    });

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
