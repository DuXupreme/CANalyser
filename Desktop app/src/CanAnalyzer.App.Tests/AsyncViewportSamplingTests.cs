using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class AsyncViewportSamplingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task RapidPans_PublishLatestRangeWithOriginalPeaksAndGapBreaks(bool step, bool markers)
        => PlotRegressionTests.OnDispatcher(async () =>
    {
        var fixture = await Create(step, markers);
        try
        {
            fixture.Host.Show();
            await Until(() => fixture.View.IsLoaded && fixture.Model.PlotArea.Width > 0);
            Assert.Same(fixture.View, fixture.Model.PlotView);
            await Settle();

            // Direct updates generate several worker requests even when redraw invalidations
            // would otherwise coalesce into one. Their completions may arrive out of order.
            for (var i = 0; i < 12; i++) Pan(fixture, 20 + i * 15, 40 + i * 15);
            Pan(fixture, 320, 340);
            await Until(() => InExpandedWindow(fixture, 320, 340));
            await Settle();

            var points = Points(fixture.Series);
            Assert.True(InExpandedWindow(fixture, 320, 340), "An obsolete viewport replaced the newest result.");
            Assert.InRange(points.Length, 2, 4_002);
            Assert.Contains(points, point => point.X == 325.123 && point.Y == 700);
            Assert.Contains(points, point => point.X == 335.321 && point.Y == -900);
            AssertOriginalSamples(fixture.Source, points);
            if (!markers)
            {
                Assert.Contains(points, point => double.IsNaN(point.X));
                AssertNoLineAcrossGap(points, 332.999, 334);
            }
            Assert.Equal(320, fixture.Model.Axes[0].ActualMinimum);
            Assert.Equal(340, fixture.Model.Axes[0].ActualMaximum);
            Assert.Null(fixture.Model.GetLastPlotException());
        }
        finally { ViewportSampling.Suspend(fixture.Model); fixture.Host.Close(); }
    });

    [Fact]
    public Task SuspendedUnloadedView_ResumesLatestRangeAndResamplesHiddenTrace()
        => PlotRegressionTests.OnDispatcher(async () =>
    {
        var fixture = await Create();
        try
        {
            fixture.Host.Show();
            await Until(() => fixture.View.IsLoaded && fixture.Model.PlotArea.Width > 0);
            Pan(fixture, 100, 120);
            await Until(() => InExpandedWindow(fixture, 100, 120));

            Pan(fixture, 400, 420);
            ViewportSampling.Suspend(fixture.Model);
            fixture.Container.Children.Remove(fixture.View);
            await Until(() => !fixture.View.IsLoaded);
            Pan(fixture, 700, 720);
            await Settle();
            Assert.False(InExpandedWindow(fixture, 700, 720), "A suspended view published newly requested data.");

            fixture.Series.IsVisible = false;
            fixture.Container.Children.Add(fixture.View);
            await Until(() => fixture.View.IsLoaded);
            ViewportSampling.Resume(fixture.Model);
            Pan(fixture, 800, 820);
            await Settle();
            Assert.False(InExpandedWindow(fixture, 800, 820), "A hidden trace was resampled.");

            fixture.Series.IsVisible = true;
            fixture.Model.InvalidatePlot(true);
            await Until(() => InExpandedWindow(fixture, 800, 820));
            await Settle();
            Assert.True(InExpandedWindow(fixture, 800, 820));
            AssertOriginalSamples(fixture.Source, Points(fixture.Series));
            Assert.Same(fixture.Model, fixture.View.ActualModel);
            Assert.Null(fixture.Model.GetLastPlotException());
        }
        finally { ViewportSampling.Suspend(fixture.Model); fixture.Host.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DataRefreshAndReset_AfterAsyncSamplingPreserveFullAutoscaleBounds(bool markers)
        => PlotRegressionTests.OnDispatcher(async () =>
    {
        var fixture = await Create(markers: markers);
        try
        {
            fixture.Host.Show();
            await Until(() => fixture.View.IsLoaded && fixture.Model.PlotArea.Width > 0);
            await Settle();
            var xAxis = fixture.Model.Axes[0]; var yAxis = fixture.Model.Axes[1];
            var originalX = (xAxis.ActualMinimum, xAxis.ActualMaximum);
            var originalY = (yAxis.ActualMinimum, yAxis.ActualMaximum);
            Assert.True(originalY.ActualMinimum <= -4000 && originalY.ActualMaximum >= 5000);

            Pan(fixture, 320, 340);
            await Until(() => InExpandedWindow(fixture, 320, 340));
            fixture.Model.InvalidatePlot(true);
            await Settle();
            Assert.Equal(originalY, (yAxis.ActualMinimum, yAxis.ActualMaximum));

            fixture.Model.ResetAllAxes();
            fixture.Model.InvalidatePlot(true);
            await Until(() => xAxis.ActualMinimum == originalX.ActualMinimum && xAxis.ActualMaximum == originalX.ActualMaximum);
            await Settle();
            Assert.Equal(originalX, (xAxis.ActualMinimum, xAxis.ActualMaximum));
            Assert.Equal(originalY, (yAxis.ActualMinimum, yAxis.ActualMaximum));
            var points = Points(fixture.Series).Where(point => double.IsFinite(point.X)).ToArray();
            Assert.Equal(fixture.Source.Time[0], points[0].X);
            Assert.Equal(fixture.Source.Time[^1], points[^1].X);
            AssertOriginalSamples(fixture.Source, points);
            Assert.Null(fixture.Model.GetLastPlotException());
        }
        finally { ViewportSampling.Suspend(fixture.Model); fixture.Host.Close(); }
    });

    [Fact]
    public Task DetachedModel_AfterPendingAsyncSampling_RendersNewHeadlessViewportSynchronously()
        => PlotRegressionTests.OnDispatcher(async () =>
    {
        var fixture = await Create();
        try
        {
            fixture.Host.Show();
            await Until(() => fixture.View.IsLoaded && fixture.Model.PlotArea.Width > 0);
            Pan(fixture, 100, 120);
            await Until(() => InExpandedWindow(fixture, 100, 120));

            // Leave a new worker request pending when the view detaches the model.
            Pan(fixture, 400, 420);
            fixture.View.Model = null;
            Assert.Null(fixture.Model.PlotView);
            fixture.Model.Axes[0].Zoom(320, 340);
            ((IPlotModel)fixture.Model).Update(false);

            // No dispatcher yield: a headless exporter needs the new data now.
            Assert.True(InExpandedWindow(fixture, 320, 340));
            var points = Points(fixture.Series);
            Assert.Contains(points, point => point.X == 325.123 && point.Y == 700);
            Assert.Contains(points, point => point.X == 335.321 && point.Y == -900);
            AssertOriginalSamples(fixture.Source, points);
            AssertNoLineAcrossGap(points, 332.999, 334);

            var canvas = new Canvas();
            ((IPlotModel)fixture.Model).Render(new OxyPlot.Wpf.CanvasRenderContext(canvas), new OxyRect(0, 0, 480, 250));
            Assert.NotEmpty(canvas.Children.Cast<UIElement>());
            Assert.Null(fixture.Model.GetLastPlotException());
            await Settle();
            Assert.Equal(points, Points(fixture.Series));
        }
        finally { ViewportSampling.Suspend(fixture.Model); fixture.Host.Close(); }
    });

    [Fact]
    public Task ModelTransfer_ResumesOrdinaryViewAndKeepsUnloadedCachedOwnerSuspended()
        => PlotRegressionTests.OnDispatcher(async () =>
    {
        var fixture = await Create();
        var ordinary = new OxyPlot.Wpf.PlotView { Width = 480, Height = 250 };
        try
        {
            fixture.Host.Show();
            await Until(() => fixture.View.IsLoaded && fixture.Model.PlotArea.Width > 0);
            Pan(fixture, 100, 120);
            await Until(() => InExpandedWindow(fixture, 100, 120));

            // The old CachedPlotView suspends after OxyPlot has already detached
            // the model. A new ordinary view has no explicit Resume callback.
            Pan(fixture, 400, 420);
            fixture.View.Model = null;
            fixture.Container.Children.Remove(fixture.View);
            fixture.Container.Children.Add(ordinary);
            await Until(() => ordinary.IsLoaded && !fixture.View.IsLoaded);
            ordinary.Model = fixture.Model;
            Pan(fixture, 320, 340);
            await Until(() => InExpandedWindow(fixture, 320, 340));
            await Settle();
            Assert.Same(ordinary, fixture.Model.PlotView);
            var ordinaryPoints = Points(fixture.Series);
            Assert.Contains(ordinaryPoints, point => point.X == 325.123 && point.Y == 700);
            Assert.Contains(ordinaryPoints, point => point.X == 335.321 && point.Y == -900);
            AssertNoLineAcrossGap(ordinaryPoints, 332.999, 334);

            // Reattaching to an unloaded CachedPlotView must establish that
            // owner's suspension, even when the previous ordinary view was active.
            ordinary.Model = null;
            fixture.Container.Children.Remove(ordinary);
            fixture.View.Model = fixture.Model;
            Assert.False(fixture.View.IsLoaded);
            Pan(fixture, 700, 720);
            await Settle();
            Assert.Equal(ordinaryPoints, Points(fixture.Series));

            fixture.Container.Children.Add(fixture.View);
            await Until(() => fixture.View.IsLoaded && InExpandedWindow(fixture, 700, 720));
            Assert.Same(fixture.View, fixture.Model.PlotView);

            // Pending work is also cancelled when the same owner unloads; direct
            // model updates cannot restart it until the Loaded callback resumes it.
            Pan(fixture, 400, 420);
            fixture.Container.Children.Remove(fixture.View);
            await Until(() => !fixture.View.IsLoaded);
            var unloadedPoints = Points(fixture.Series);
            Pan(fixture, 800, 820);
            await Settle();
            Assert.Equal(unloadedPoints, Points(fixture.Series));
            fixture.Container.Children.Add(fixture.View);
            await Until(() => fixture.View.IsLoaded && InExpandedWindow(fixture, 800, 820));
            AssertOriginalSamples(fixture.Source, Points(fixture.Series));
            Assert.Null(fixture.Model.GetLastPlotException());
        }
        finally
        {
            ordinary.Model = null;
            ViewportSampling.Suspend(fixture.Model);
            fixture.Host.Close();
        }
    });

    private sealed record Fixture(RenderedSeriesData Source, PlotModel Model, Series Series,
        CachedPlotView View, Grid Container, Window Host);

    private static async Task<Fixture> Create(bool step = false, bool markers = false)
    {
        var source = Source();
        var options = new PlotViewOptions { UseDownsampling = true, MaxPointsPerTrace = 2_000,
            StepPlot = step, MarkersOnly = markers };
        var series = await Task.Run(() => PlotSeriesRenderer.Build(source, options));
        var model = new PlotModel();
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Key = "Y1" });
        model.Series.Add(series);
        var view = new CachedPlotView { Model = model, Width = 480, Height = 250 };
        ViewportSampling.Attach(model, [source], true, options.MaxPointsPerTrace);
        var container = new Grid(); container.Children.Add(view);
        var host = new Window { Content = container, Width = 520, Height = 300,
            Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        return new(source, model, series, view, container, host);
    }

    private static RenderedSeriesData Source()
    {
        var x = new double[1_000_000]; var y = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            var sample = i < 333_000 ? i : i + 1_000;
            x[i] = sample / 1000d; y[i] = Math.Sin(sample * 0.013);
            if (sample == 325_123) y[i] = 700;
            if (sample == 335_321) y[i] = -900;
        }
        y[0] = -4000; y[^1] = 5000;
        return new("Million samples", x, y, "Y1", OxyColors.Blue)
        { Gaps = [new MeasurementGap(332.999, 334)] };
    }

    private static void Pan(Fixture fixture, double minimum, double maximum)
    {
        fixture.Model.Axes[0].Zoom(minimum, maximum);
        ((IPlotModel)fixture.Model).Update(false);
        fixture.Model.InvalidatePlot(false);
    }

    private static bool InExpandedWindow(Fixture fixture, double minimum, double maximum)
    {
        var points = Points(fixture.Series).Where(point => double.IsFinite(point.X)).ToArray();
        var margin = (maximum - minimum) * 0.25 + 0.002;
        return points.Length > 0 && points.All(point => point.X >= minimum - margin && point.X <= maximum + margin)
            && points[0].X <= minimum && points[^1].X >= maximum;
    }

    private static DataPoint[] Points(Series series) => series is LineSeries line ? line.Points.ToArray()
        : ((ScatterSeries)series).Points.Select(point => new DataPoint(point.X, point.Y)).ToArray();

    private static void AssertOriginalSamples(RenderedSeriesData source, IEnumerable<DataPoint> points)
    {
        foreach (var point in points)
        {
            if (!double.IsFinite(point.X)) continue;
            var index = Array.BinarySearch(source.Time, point.X);
            Assert.True(index >= 0, "Rendered timestamp is not an original source sample.");
            Assert.Equal(source.Value[index], point.Y);
        }
    }

    private static void AssertNoLineAcrossGap(IEnumerable<DataPoint> points, double minimum, double maximum)
    {
        DataPoint? previous = null;
        foreach (var point in points)
        {
            if (!double.IsFinite(point.X)) { previous = null; continue; }
            if (previous is { } before)
                Assert.False(before.X <= minimum && point.X >= maximum, "A rendered line bridges the measurement gap.");
            previous = point;
        }
    }

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
