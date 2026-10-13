using System.Collections.Specialized;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class VisiblePlotRebuildTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Builder_RawViewIncludesEveryVisibleSampleAndAdjacentSamples(bool step, bool markers)
    {
        using var data = PlotRegressionTests.Dataset(1_000_000);
        var source = data.SignalSeriesByLabel["test"];
        var panel = Assert.Single(new PlotModelBuilder().Build(data,
            [new PlotGroup { Signals = ["test"] }],
            new PlotViewOptions { UseDownsampling = false, StepPlot = step, MarkersOnly = markers },
            [new PlotViewport(500, 500.004)]));

        AssertRawSamples(panel, source, 499_999, 7);
        Assert.Same(source.Time, panel.SeriesData[0].Time);
        Assert.Same(source.Value, panel.SeriesData[0].Value);
        Assert.Equal(1_000_000, panel.SeriesData[0].Time.Length);
    }

    [Fact]
    public void Builder_NoVisibleRangeKeepsInitialPlotBounded()
    {
        using var data = PlotRegressionTests.Dataset(1_000_000);
        var panel = Assert.Single(new PlotModelBuilder().Build(data,
            [new PlotGroup { Signals = ["test"] }],
            new PlotViewOptions { UseDownsampling = false, MaxPointsPerTrace = 500 }));

        var points = Points(panel);
        Assert.InRange(points.Count, 2, 500);
        Assert.Equal(0, points[0].X);
        Assert.Equal(999.999, points[^1].X);
        Assert.Equal(1_000_000, panel.SeriesData[0].Time.Length);
    }

    [Fact]
    public Task DisableDownsampling_PreservesAxesVisibilityAndCompleteSourceBuffers() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset(1_000_000);
        var vm = await CreatePlots(data);
        var original = Assert.Single(vm.PlotPanels);
        XAxis(original).Zoom(500, 500.004);
        YAxis(original).Zoom(-2, 2);
        Assert.Single(original.PlotModel.Series).IsVisible = false;

        vm.UseDownsampling = false;
        await WaitForIdle(vm);

        Assert.False(vm.UseDownsampling);
        var current = Assert.Single(vm.PlotPanels);
        Assert.NotSame(original, current);
        Assert.Equal(500, XAxis(current).ActualMinimum);
        Assert.Equal(500.004, XAxis(current).ActualMaximum);
        Assert.Equal(-2, YAxis(current).ActualMinimum);
        Assert.Equal(2, YAxis(current).ActualMaximum);
        Assert.False(Assert.Single(current.PlotModel.Series).IsVisible);
        var source = data.SignalSeriesByLabel["test"];
        AssertRawSamples(current, source, 499_999, 7);
        Assert.Same(source.Time, current.SeriesData[0].Time);
        Assert.Same(source.Value, current.SeriesData[0].Value);
    });

    [Fact]
    public Task DisableDownsampling_UsesEachIndependentSubplotRange() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset(1_000_000);
        var vm = await CreatePlots(data, panelCount: 2);
        vm.LinkXAxisAcrossPanels = false;
        vm.LinkYAxisAcrossPanels = false;
        XAxis(vm.PlotPanels[0]).Zoom(100, 100.003);
        XAxis(vm.PlotPanels[1]).Zoom(800, 800.005);

        vm.UseDownsampling = false;
        await WaitForIdle(vm);

        Assert.False(vm.UseDownsampling);
        Assert.Equal(2, vm.PlotPanels.Count);
        var source = data.SignalSeriesByLabel["test"];
        AssertRawSamples(vm.PlotPanels[0], source, 99_999, 6);
        AssertRawSamples(vm.PlotPanels[1], source, 799_999, 8);
        Assert.Equal(100, XAxis(vm.PlotPanels[0]).ActualMinimum);
        Assert.Equal(800, XAxis(vm.PlotPanels[1]).ActualMinimum);
    });

    [Theory]
    [InlineData("pan")]
    [InlineData("zoom")]
    [InlineData("reset")]
    [InlineData("y")]
    public Task AxisNavigation_RestoresSharedDownsampling(string navigation) => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset(100_000);
        var vm = await CreatePlots(data);
        XAxis(Assert.Single(vm.PlotPanels)).Zoom(50, 50.004);
        vm.UseDownsampling = false;
        await WaitForIdle(vm);
        Assert.False(vm.DisplayLimits.UseDownsampling);

        var panel = Assert.Single(vm.PlotPanels);
        switch (navigation)
        {
            case "pan": XAxis(panel).Zoom(60, 60.004); break;
            case "zoom": XAxis(panel).Zoom(50.001, 50.003); break;
            case "reset": XAxis(panel).Reset(); break;
            case "y": YAxis(panel).Zoom(-0.2, 0.2); break;
            default: throw new InvalidOperationException(navigation);
        }

        Assert.True(vm.DisplayLimits.UseDownsampling);
        Assert.True(vm.UseDownsampling);
        Assert.Same(panel, Assert.Single(vm.PlotPanels));
        await WaitForIdle(vm);
        Assert.True(vm.UseDownsampling);
        // OxyPlot's active drag and the undo history both keep references to these axes.
        Assert.Same(panel, Assert.Single(vm.PlotPanels));
    });

    [Fact]
    public Task NavigationDuringRawBuild_KeepsDispatcherResponsiveAndRejectsObsoleteResult() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset(1_000_000);
        using var builder = new BlockingRawBuilder();
        var vm = await CreatePlots(data, builder);
        var original = Assert.Single(vm.PlotPanels);
        XAxis(original).Zoom(500, 500.004);
        var published = new List<PlotPanelModel>();
        vm.PlotPanels.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add)
                published.AddRange(args.NewItems!.Cast<PlotPanelModel>());
        };

        try
        {
            vm.UseDownsampling = false;
            await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsBusy);
            Assert.NotEqual(Environment.CurrentManagedThreadId, builder.WorkerThreadId);
            Assert.False(builder.WasReleased);
            // The continuation itself runs on the dispatcher while the worker is still blocked.
            Assert.Same(original, Assert.Single(vm.PlotPanels));
            XAxis(original).Zoom(700, 700.006);
            YAxis(original).Zoom(-3, 3);
            Assert.True(vm.DisplayLimits.UseDownsampling);
        }
        finally
        {
            builder.Release();
        }

        await WaitForIdle(vm);
        var current = Assert.Single(vm.PlotPanels);
        Assert.Same(original, current);
        Assert.Equal(700, XAxis(current).ActualMinimum);
        Assert.Equal(700.006, XAxis(current).ActualMaximum);
        Assert.Equal(-3, YAxis(current).ActualMinimum);
        Assert.Equal(3, YAxis(current).ActualMaximum);
        Assert.True(vm.UseDownsampling);
        Assert.NotNull(builder.RawPanels);
        Assert.Empty(published);
    });

    [Fact]
    public Task ResetDuringRawBuild_KeepsOriginalModelAndAutomaticAxisRange() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset(100_000);
        using var builder = new BlockingRawBuilder();
        var vm = await CreatePlots(data, builder);
        var original = Assert.Single(vm.PlotPanels);
        var axis = XAxis(original);
        axis.Zoom(50, 50.004);
        try
        {
            vm.UseDownsampling = false;
            await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            axis.Reset();
            ((IPlotModel)original.PlotModel).Update(true);
            Assert.True(vm.UseDownsampling);
        }
        finally { builder.Release(); }

        await WaitForIdle(vm);
        Assert.Same(original, Assert.Single(vm.PlotPanels));
        Assert.False(Assert.IsType<ViewportAxis>(axis).HasUserRange);
        Assert.True(axis.ActualMinimum < 0);
        Assert.True(axis.ActualMaximum > 99.999);
    });

    [Fact]
    public Task NavigationWithPendingStyleChange_PublishesStyleAtLatestView() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset(100_000);
        using var builder = new BlockingRawBuilder();
        var vm = await CreatePlots(data, builder);
        var original = Assert.Single(vm.PlotPanels);
        XAxis(original).Zoom(50, 50.004);
        try
        {
            vm.UseDownsampling = false;
            await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.StepPlot = true;
            XAxis(original).Zoom(70, 70.006);
            Assert.True(vm.UseDownsampling);
        }
        finally { builder.Release(); }

        await WaitForIdle(vm);
        var current = Assert.Single(vm.PlotPanels);
        Assert.IsType<StairStepSeries>(Assert.Single(current.PlotModel.Series));
        Assert.Equal(70, XAxis(current).ActualMinimum);
        Assert.Equal(70.006, XAxis(current).ActualMaximum);
        Assert.True(vm.UseDownsampling);
    });

    private static async Task<AnalysisViewModel> CreatePlots(CanDataset data, IPlotModelBuilder? builder = null, int panelCount = 1)
    {
        var vm = PlotRegressionTests.ViewModel(builder);
        vm.LoadDataset(data);
        vm.ApplyViewOptions(new PlotViewOptions { UseDownsampling = true, MaxPointsPerTrace = 500 });
        vm.LoadPlotGroups(Enumerable.Range(0, panelCount).Select(i => new PlotGroup
        {
            Title = $"Panel {i + 1}", Signals = ["test"]
        }).ToArray());
        await PlotRegressionTests.Build(vm);
        await WaitForIdle(vm);
        foreach (var panel in vm.PlotPanels) ((IPlotModel)panel.PlotModel).Update(true);
        return vm;
    }

    private static async Task WaitForIdle(AnalysisViewModel vm)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (vm.IsBusy) await Task.Delay(10, timeout.Token);
    }

    private static Axis XAxis(PlotPanelModel panel) => panel.PlotModel.Axes.Single(a => a.Position == AxisPosition.Bottom);
    private static Axis YAxis(PlotPanelModel panel) => panel.PlotModel.Axes.Single(a => a.Position == AxisPosition.Left);

    private static IReadOnlyList<DataPoint> Points(PlotPanelModel panel) => Assert.Single(panel.PlotModel.Series) switch
    {
        LineSeries line => line.Points,
        ScatterSeries scatter => scatter.Points.Select(p => new DataPoint(p.X, p.Y)).ToArray(),
        _ => throw new InvalidOperationException("Unexpected plot series type.")
    };

    private static void AssertRawSamples(PlotPanelModel panel, SignalSeries source, int first, int count)
    {
        var points = Points(panel);
        Assert.Equal(count, points.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(source.Time[first + i], points[i].X);
            Assert.Equal(source.Value[first + i], points[i].Y);
        }
    }

    private sealed class BlockingRawBuilder : IPlotModelBuilder, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WorkerThreadId { get; private set; }
        public bool WasReleased => _release.IsSet;
        public IReadOnlyList<PlotPanelModel>? RawPanels { get; private set; }

        public IReadOnlyList<PlotPanelModel> Build(CanDataset dataset, IReadOnlyList<PlotGroup> groups,
            PlotViewOptions options, IReadOnlyList<PlotViewport?>? viewports = null)
        {
            if (!options.UseDownsampling)
            {
                WorkerThreadId = Environment.CurrentManagedThreadId;
                Started.TrySetResult();
                if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Raw plot worker was not released.");
            }
            var panels = new PlotModelBuilder().Build(dataset, groups, options, viewports);
            if (!options.UseDownsampling) RawPanels = panels;
            return panels;
        }

        public void Release() => _release.Set();
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }
}
