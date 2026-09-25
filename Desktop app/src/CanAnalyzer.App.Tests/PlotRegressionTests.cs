using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class PlotRegressionTests
{
    private sealed class PlotWindow : IPlotWindowService
    {
        public IReadOnlyList<PlotPanelModel>? Panels;
        public int Thread;
        public void ShowPlots(IReadOnlyList<PlotPanelModel> panels, int subplotHeight, int maxPointsPerTrace,
            bool useDownsampling, bool linkXAxisAcrossPanels, bool linkYAxisAcrossPanels, DateTimeOffset? startTimeUtc)
        { Panels = panels; Thread = Environment.CurrentManagedThreadId; }
    }

    [Fact]
    public Task DetachedWindowBuild_IsAsynchronousAndPublishesOnDispatcher() => OnDispatcher(async () =>
    {
        using var data = Dataset(); var window = new PlotWindow(); var telemetry = new AnalyticsRegressionTests.Telemetry();
        var vm = new AnalysisViewModel(new SlowBuilder(), null!, window, null!, new XAxisSyncService(), telemetry,
            new ActiveUsageViewModel(telemetry), NullLogger<AnalysisViewModel>.Instance);
        vm.LoadDataset(data); vm.LoadPlotGroups([new PlotGroup { Signals = ["test"] }]);
        await Build(vm);
        var pending = vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
        Assert.False(pending.IsCompleted);
        Assert.True(vm.IsBusy);
        await pending;
        Assert.Equal(Environment.CurrentManagedThreadId, window.Thread);
        Assert.Single(window.Panels!); Assert.False(vm.IsBusy);
    });
    private sealed class SlowBuilder : IPlotModelBuilder
    {
        public readonly List<int> Requests = [];
        public IReadOnlyList<PlotPanelModel> Build(CanDataset dataset, IReadOnlyList<PlotGroup> groups, PlotViewOptions options)
        {
            Requests.Add(options.MaxPointsPerTrace);
            Thread.Sleep(150);
            return new PlotModelBuilder().Build(dataset, groups, options);
        }
    }

    [Fact]
    public Task LiveEdits_LeaveDispatcherResponsiveAndPublishLatestOptions() => OnDispatcher(async () =>
    {
        using var data = Dataset(); var builder = new SlowBuilder(); var vm = ViewModel(builder);
        vm.LoadDataset(data); vm.ApplyViewOptions(new PlotViewOptions { UseDownsampling = true });
        vm.LoadPlotGroups([new PlotGroup { Signals = ["test"] }]);
        await Build(vm);
        while (vm.IsBusy) await Task.Delay(10);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        vm.MaxPointsPerTrace = 300;
        Assert.True(watch.ElapsedMilliseconds < 100, "Live edit blocked the UI for the whole build.");
        await Task.Delay(30);
        vm.MaxPointsPerTrace = 600;
        vm.ShowLegend = false;
        var ticks = 0;
        while (vm.IsBusy) { ticks++; await Task.Delay(10); }
        Assert.True(ticks > 2);
        Assert.Equal(600, builder.Requests[^1]);
        Assert.False(Assert.Single(vm.PlotPanels).PlotModel.IsLegendVisible);
    });
    internal static Task OnDispatcher(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await work(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    internal static CanDataset Dataset(int count = 10000)
    {
        var series = new SignalSeries("test", Enumerable.Range(0, count).Select(i => i / 1000d).ToArray(),
            Enumerable.Range(0, count).Select(i => Math.Sin(i / 100d)).ToArray());
        return new CanDataset { RawFrames = [], DecodedSamples = [], MessageSummaries = [],
            SignalLabels = [series.Label], SignalSeriesByLabel = new Dictionary<string, SignalSeries> { [series.Label] = series },
            SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries> { [series.Identity] = series },
            Diagnostics = new(0, 0, 1, 0, 0, "") };
    }

    internal static AnalysisViewModel ViewModel(IPlotModelBuilder? builder = null) => new(builder ?? new PlotModelBuilder(),
        null!, null!, null!, new XAxisSyncService(), null!, new ActiveUsageViewModel(null!), NullLogger<AnalysisViewModel>.Instance);

    internal static Task Build(AnalysisViewModel vm) => (Task)typeof(AnalysisViewModel)
        .GetMethod("RebuildPlotsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [false])!;

    [Fact]
    public Task LegendEdit_PreservesModelsAxesVisibilityAndSourceBuffers() => OnDispatcher(async () =>
    {
        using var data = Dataset(1_000_000);
        var vm = ViewModel(); vm.LoadDataset(data);
        vm.ApplyViewOptions(new PlotViewOptions { UseDownsampling = true });
        vm.LoadPlotGroups([new PlotGroup { Signals = data.SignalLabels.ToList(), LockYAxis = true }]);
        await Build(vm);
        while (vm.IsBusy) await Task.Delay(10);
        var panel = Assert.Single(vm.PlotPanels); var line = Assert.IsAssignableFrom<LineSeries>(Assert.Single(panel.PlotModel.Series));
        panel.PlotModel.Axes[0].Zoom(2, 4); line.IsVisible = false;
        var source = data.SignalSeriesByLabel["test"];
        Assert.Same(source.Time, panel.SeriesData[0].Time); Assert.Same(source.Value, panel.SeriesData[0].Value);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        vm.ShowLegend = false;
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 100_000);
        Assert.Same(panel, Assert.Single(vm.PlotPanels)); Assert.False(line.IsVisible);
        Assert.Equal(2, panel.PlotModel.Axes[0].ActualMinimum); Assert.Equal(4, panel.PlotModel.Axes[0].ActualMaximum);
        Assert.False(panel.PlotModel.IsLegendVisible);
    });

    [Fact]
    public Task DetachedStyleChange_PreservesDataBeyondZoomAndGlobalEndpoints() => OnDispatcher(() =>
    {
        using var data = Dataset(100_000);
        var panel = Assert.Single(new PlotModelBuilder().Build(data,
            [new PlotGroup { Signals = data.SignalLabels.ToList() }], new PlotViewOptions { UseDownsampling = true }));
        var window = (PlotPanelsWindow)RuntimeHelpers.GetUninitializedObject(typeof(PlotPanelsWindow));
        typeof(PlotPanelsWindow).GetField("_useDownsampling", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        typeof(PlotPanelsWindow).GetField("_maxPointsPerTrace", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, 5000);
        var method = typeof(PlotPanelsWindow).GetMethod("BuildSeries", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var line = (LineSeries)method.Invoke(window, [panel.SeriesData[0], 10d, 11d])!;
        Assert.Equal(0, line.Points[0].X); Assert.Equal(99.999, line.Points[^1].X, 6);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Transformations_DoNotModifySharedMeasurementValues() => OnDispatcher(() =>
    {
        using var data = Dataset(); var source = data.SignalSeriesByLabel["test"];
        var expected = source.Value.ToArray();
        var group = new PlotGroup { Signals = ["test"], Offsets = new Dictionary<string, double> { ["test"] = 5 } };
        var panel = Assert.Single(new PlotModelBuilder().Build(data, [group], new PlotViewOptions { NormalizeSignals = true, TimeStart = 2, TimeEnd = 3 }));
        Assert.Equal(expected, source.Value);
        Assert.All(panel.SeriesData[0].Value, v => Assert.InRange(v, 0, 1));
        Assert.Equal(2, panel.SeriesData[0].Time[0]); Assert.Equal(3, panel.SeriesData[0].Time[^1]);
        return Task.CompletedTask;
    });
}
