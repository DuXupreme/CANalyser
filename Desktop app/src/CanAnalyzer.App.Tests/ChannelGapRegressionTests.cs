using System.Reflection;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ChannelGapRegressionTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void BusyOtherChannelCannotHideGrayRegionOrJoinInterruptedTrace(bool downsample, bool step)
    {
        using var dataset = Dataset();
        var panel = Assert.Single(new PlotModelBuilder().Build(dataset,
            [new PlotGroup { Signals = ["machine", "other bus"], LockYAxis = true }],
            new PlotViewOptions { UseDownsampling = downsample, StepPlot = step, MaxPointsPerTrace = 200 },
            [new PlotViewport(2, 8)]));
        var gap = Assert.Single(panel.PlotModel.Annotations.OfType<RectangleAnnotation>());
        Assert.Equal(3, gap.MinimumX); Assert.Equal(7, gap.MaximumX);
        Assert.Contains("kanaal 1", gap.Text);
        Assert.Equal(OxyColor.FromAColor(35, OxyColors.Gray), gap.Fill);
        AssertBroken(panel.PlotModel.Series[0]);
        Assert.DoesNotContain(((LineSeries)panel.PlotModel.Series[1]).Points, point => double.IsNaN(point.X));

        // Zooming entirely into the empty interval must not connect its two edge samples.
        panel.PlotModel.Axes[0].Zoom(4, 6);
        ((IPlotModel)panel.PlotModel).Update(true);
        AssertBroken(panel.PlotModel.Series[0]);
        Assert.Same(gap, Assert.Single(panel.PlotModel.Annotations));

        // Pan away, then reset: the cached overview and viewport sampling must retain the break.
        panel.PlotModel.Axes[0].Zoom(8, 9);
        ((IPlotModel)panel.PlotModel).Update(false);
        panel.PlotModel.ResetAllAxes();
        ((IPlotModel)panel.PlotModel).Update(true);
        AssertBroken(panel.PlotModel.Series[0]);
        Assert.DoesNotContain(((LineSeries)panel.PlotModel.Series[1]).Points, point => double.IsNaN(point.X));
        Assert.Null(panel.PlotModel.GetLastPlotException());
    }

    [Theory]
    [InlineData("other bus")]
    [InlineData("diagnostics")]
    public void ContinuousChannelDoesNotInheritAnotherChannelsGrayRegions(string label)
    {
        using var dataset = Dataset();
        var panel = Assert.Single(new PlotModelBuilder().Build(dataset,
            [new PlotGroup { Signals = [label] }], new PlotViewOptions { UseDownsampling = true }));
        Assert.Empty(panel.PlotModel.Annotations);
        Assert.Empty(Assert.Single(panel.SeriesData).Gaps);
        Assert.DoesNotContain(((LineSeries)Assert.Single(panel.PlotModel.Series)).Points, point => double.IsNaN(point.X));
    }

    [Fact]
    public void MarkerModeKeepsTheGrayInterruption()
    {
        using var dataset = Dataset();
        var panel = Assert.Single(new PlotModelBuilder().Build(dataset,
            [new PlotGroup { Signals = ["machine"] }],
            new PlotViewOptions { MarkersOnly = true, UseDownsampling = true, MaxPointsPerTrace = 200 }));
        var gap = Assert.Single(panel.PlotModel.Annotations.OfType<RectangleAnnotation>());
        Assert.Equal(3, gap.MinimumX); Assert.Equal(7, gap.MaximumX);
        var points = Assert.IsAssignableFrom<ScatterSeries>(Assert.Single(panel.PlotModel.Series)).Points;
        Assert.DoesNotContain(points, point => point.X > 3 && point.X < 7);
    }

    [Fact]
    public void LegacyReportsKeepTheirGrayRegionsAndLineBreaks()
    {
        using var dataset = Dataset(new ImportReport("legacy", 0, 0, 0, 0, []) { Gaps = [new(3, 7)] });
        var panel = Assert.Single(new PlotModelBuilder().Build(dataset,
            [new PlotGroup { Signals = ["machine"] }], new PlotViewOptions()));
        Assert.Equal("Geen data: 4 s", Assert.Single(panel.PlotModel.Annotations.OfType<RectangleAnnotation>()).Text);
        AssertBroken(Assert.Single(panel.PlotModel.Series));
    }

    [Fact]
    public void WindowingRetainsTheOriginalChannelAndExactTimestamps()
    {
        var source = Series("1", "position", [0, 1.000000001, 2.000000002, 3]);
        var windowed = (SignalSeries)typeof(JoystickAnalyticsViewModel)
            .GetMethod("Windowed", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source, ((double?)1, (double?)2.5)])!;
        Assert.Equal(source.Identity, windowed.Identity);
        Assert.Equal(source.Label, windowed.Label);
        Assert.Equal(new long[] { 1_000_000_001, 2_000_000_002 }, windowed.TimestampNanoseconds);
    }

    [Fact]
    public Task ActiveUsageDoesNotCountAChannelGapWhileAnotherBusContinues() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var dataset = Dataset();
        var vm = new ActiveUsageViewModel(new AnalyticsRegressionTests.Telemetry());
        vm.LoadDataset(dataset);
        vm.ActivitySignal = "machine";
        vm.SocSignal = null;
        vm.Threshold = "0";
        vm.MinimumSeconds = "0";
        vm.MaxGapSeconds = "10"; // The import gap must win even when sample interpolation would allow it.
        await vm.CalculateCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult, vm.Status);
        var result = vm.ExportResult!;
        Assert.Contains(result.Intervals, interval => interval.StartSeconds == 3 && interval.EndSeconds == 7 &&
            interval.State == UsageState.Unknown);
        Assert.DoesNotContain(result.Intervals, interval => interval.State == UsageState.Active &&
            interval.StartSeconds < 7 && interval.EndSeconds > 3);
    });

    private static void AssertBroken(Series series)
    {
        var points = Assert.IsAssignableFrom<LineSeries>(series).Points;
        Assert.Contains(points, point => double.IsNaN(point.X));
        for (var index = 1; index < points.Count; index++)
            Assert.False(double.IsFinite(points[index - 1].X) && double.IsFinite(points[index].X) &&
                points[index - 1].X <= 3 && points[index].X >= 7,
                "A rendered segment bridges the known interruption.");
    }

    private static SignalSeries Series(string channel, string label, double[] time) => new(
        new SignalIdentity(channel, CanFrameFormat.Classic, false, 1, label, "value"),
        time.Select(value => checked((long)Math.Round(value * 1e9))).ToArray(),
        time.Select(_ => 1000d).ToArray(), label);

    private static CanDataset Dataset(ImportReport? report = null)
    {
        var continuous = Enumerable.Range(0, 10_001).Select(index => index / 1000d).ToArray();
        var interrupted = continuous.Where(time => time <= 3 || time >= 7).ToArray();
        SignalSeries[] series = [Series("1", "machine", interrupted), Series("2", "other bus", continuous), Series("9", "diagnostics", continuous)];
        return new()
        {
            RawFrames = [], DecodedSamples = [], MessageSummaries = [],
            SignalLabels = series.Select(signal => signal.Label).ToArray(),
            SignalSeriesByLabel = series.ToDictionary(signal => signal.Label),
            SignalSeriesByIdentity = series.ToDictionary(signal => signal.Identity),
            Diagnostics = new(0, 0, series.Length, 0, 0, ""),
            ImportReport = report ?? new ImportReport("MDF4", 0, 0, 0, 0, [])
            {
                Gaps = [],
                GapsByChannel = new Dictionary<string, IReadOnlyList<MeasurementGap>>(StringComparer.OrdinalIgnoreCase)
                { ["1"] = [new(3, 7)], ["2"] = [], ["9"] = [] }
            }
        };
    }
}
