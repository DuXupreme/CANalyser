using System.Reflection;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class DetachedViewportTests
{
    [Fact]
    public Task RawRefresh_IsQueuedAndOnlyUsesVisibleSourcePoints() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset(100_000);
        var limits = new DisplayLimits { UseDownsampling = true, MaxPointsPerTrace = 200 };
        var options = new PlotViewOptions { UseDownsampling = true, MaxPointsPerTrace = 200 };
        var panels = new PlotModelBuilder().Build(data, [new PlotGroup { Signals = ["test"] }], options);
        var panel = Assert.Single(panels);
        var model = panel.PlotModel;
        ((IPlotModel)model).Update(true);
        model.Axes[0].Zoom(10, 10.05);
        ((IPlotModel)model).Update(false);
        var previous = Assert.Single(model.Series);
        var window = new PlotPanelsWindow(panels, options, limits, false, false, null);
        try
        {
            window.UseDownsampling = false;
            Assert.True(window.IsBusy);
            Assert.Same(previous, Assert.Single(model.Series));
            await WaitForBuild(window);
            var raw = Assert.IsAssignableFrom<LineSeries>(Assert.Single(model.Series));
            Assert.NotSame(previous, raw);
            Assert.InRange(raw.Points.Count, 51, 53);
            Assert.Contains(raw.Points, point => point.X == 10.025);
            Assert.All(raw.Points, point => Assert.InRange(point.X, 9.999, 10.051));
            Assert.Equal(10, model.Axes[0].ActualMinimum);
            Assert.Equal(10.05, model.Axes[0].ActualMaximum);
            Assert.False(window.UseDownsampling);
            Assert.True(limits.UseDownsampling);
            Assert.Same(data.SignalSeriesByLabel["test"].Time, panel.SeriesData[0].Time);

            window.StepPlot = true;
            Assert.True(window.IsBusy);
            await WaitForBuild(window);
            Assert.IsAssignableFrom<StairStepSeries>(Assert.Single(model.Series));
            Assert.False(window.UseDownsampling);
            Assert.True(limits.UseDownsampling);
            Assert.Equal(string.Empty, window.PlotStatus);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task NavigationDuringRawBuild_ReenablesSamplingAndKeepsNewestView() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset(100_000);
        var limits = new DisplayLimits { UseDownsampling = true, MaxPointsPerTrace = 200 };
        var options = new PlotViewOptions { UseDownsampling = true, MaxPointsPerTrace = 200 };
        var panels = new PlotModelBuilder().Build(data, [new PlotGroup { Signals = ["test"] }], options);
        var model = panels[0].PlotModel;
        ((IPlotModel)model).Update(true);
        model.Axes[0].Zoom(10, 10.05);
        var window = new PlotPanelsWindow(panels, options, limits, false, false, null);
        try
        {
            window.UseDownsampling = false;
            // LatestWorkQueue yields before starting, so this invalidates the pending raw request.
            model.Axes[0].Zoom(70, 72);
            Assert.True(limits.UseDownsampling);
            window.MarkersOnly = true;
            await WaitForBuild(window);
            ((IPlotModel)model).Update(true);
            var sampled = Assert.IsAssignableFrom<ScatterSeries>(Assert.Single(model.Series));
            Assert.InRange(sampled.Points.Count, 1, 200);
            Assert.Equal(70, model.Axes[0].ActualMinimum);
            Assert.Equal(72, model.Axes[0].ActualMaximum);
            Assert.All(sampled.Points, point => Assert.InRange(point.X, 69.999, 72.001));

            window.UseDownsampling = false;
            await WaitForBuild(window);
            model.Axes[1].Zoom(-0.1, 0.1);
            Assert.True(limits.UseDownsampling);
            await WaitForBuild(window);
            Assert.Equal(-0.1, model.Axes[1].ActualMinimum);
            Assert.Equal(0.1, model.Axes[1].ActualMaximum);

            window.UseDownsampling = false;
            model.ResetAllAxes();
            Assert.True(limits.UseDownsampling);
            await WaitForBuild(window);
            Assert.False(Assert.IsType<ViewportAxis>(model.Axes[0]).HasUserRange);
            ((IPlotModel)model).Update(true);
            Assert.True(model.Axes[0].ActualMinimum < 1);
            Assert.True(model.Axes[0].ActualMaximum > 99);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ClosedWindow_DiscardsPendingRefresh() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset();
        var limits = new DisplayLimits { UseDownsampling = true };
        var options = new PlotViewOptions { UseDownsampling = true };
        var panels = new PlotModelBuilder().Build(data, [new PlotGroup { Signals = ["test"] }], options);
        var model = panels[0].PlotModel;
        ((IPlotModel)model).Update(true);
        var initial = Assert.Single(model.Series);
        var window = new PlotPanelsWindow(panels, options, limits, false, false, null);
        window.StepPlot = true;
        window.Close();
        // Wait for the queued work itself: the closed window no longer raises UI status changes.
        var queue = typeof(PlotPanelsWindow).GetField("_visualBuilds", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;
        var running = queue.GetType().GetProperty("IsRunning")!;
        while ((bool)running.GetValue(queue)!) await Task.Delay(10);
        Assert.Same(initial, Assert.Single(model.Series));
    });

    private static async Task WaitForBuild(PlotPanelsWindow window)
    {
        while (window.IsBusy) await Task.Delay(10);
        Assert.Equal(string.Empty, window.PlotStatus);
    }
}
