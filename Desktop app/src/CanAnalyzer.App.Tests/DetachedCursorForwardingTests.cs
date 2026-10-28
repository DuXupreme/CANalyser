using System.Reflection;
using System.Runtime.CompilerServices;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using OxyPlot;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class DetachedCursorForwardingTests
{
    [Fact]
    public Task ReturningToUnchangedDetachedTimeRestoresWatchAfterAnotherCursorSource() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var signal = new SignalSeries("test", [0, 1, 2], [10, 20, 30]);
        using var dataset = new CanDataset
        {
            RawFrames = [], MessageSummaries = [], SignalLabels = [signal.Label],
            SignalSeriesByLabel = new Dictionary<string, SignalSeries> { [signal.Label] = signal },
            SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries> { [signal.Identity] = signal },
            DecodedSamples = Enumerable.Range(0, 3).Select(i => new DecodedSignalSample(
                i * 1_000_000_000L, i, i, signal.Identity, (i + 1) * 10, (i + 1) * 10, "unit")).ToArray(),
            Diagnostics = new(0, 0, 1, 0, 0, "")
        };
        var windows = new RecordingWindows();
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        using var vm = new AnalysisViewModel(new PlotModelBuilder(), null!, windows, null!, new XAxisSyncService(),
            telemetry, new ActiveUsageViewModel(telemetry), NullLogger<AnalysisViewModel>.Instance, new DisplayLimits());
        var watch = new BusmasterViewModel(new DisplayLimits());
        watch.ApplySignalWatchOptions(new() { Signals = [signal.Identity] });
        watch.LoadDataset(dataset);
        System.ComponentModel.PropertyChangedEventHandler sharedCursor = (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CursorTime)) watch.SetWatchCursorAt(vm.CursorTime);
        };
        vm.PropertyChanged += sharedCursor;
        try
        {
            vm.LoadDataset(dataset);
            vm.LoadPlotGroups([new PlotGroup { Signals = [signal.Label] }]);
            await vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
            var detachedCursor = Assert.Single(windows.Cursors);
            detachedCursor(1);
            await watch.WaitForWatchValuesAsync();
            Assert.Equal("20", Assert.Single(watch.WatchedSignalRows).PhysicalValue);

            // TimePlotView and the message list update Signal Watch directly;
            // Analysis.CursorTime still contains the earlier detached value.
            watch.SetWatchCursorAt(2);
            await watch.WaitForWatchValuesAsync();
            Assert.Equal(1, vm.CursorTime);
            Assert.Equal("30", Assert.Single(watch.WatchedSignalRows).PhysicalValue);

            detachedCursor(1);
            await watch.WaitForWatchValuesAsync();
            Assert.Equal(1, vm.CursorTime);
            Assert.Equal("20", Assert.Single(watch.WatchedSignalRows).PhysicalValue);
        }
        finally
        {
            vm.PropertyChanged -= sharedCursor;
            await watch.CancelAndWaitAsync(); watch.ClearDataset();
            await vm.CancelAndWaitAsync();
            await vm.ActiveUsage.CancelAndWaitAsync();
        }
    });

    [Theory]
    [InlineData("clear")]
    [InlineData("replace")]
    [InlineData("dispose")]
    public Task CallbackBelongsOnlyToTheDatasetThatOpenedItsWindow(string transition) => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var original = PlotRegressionTests.Dataset(20);
        using var replacement = PlotRegressionTests.Dataset(20); // Same labels/times, different recording.
        var windows = new RecordingWindows();
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        using var vm = new AnalysisViewModel(new PlotModelBuilder(), null!, windows, null!, new XAxisSyncService(),
            telemetry, new ActiveUsageViewModel(telemetry), NullLogger<AnalysisViewModel>.Instance, new DisplayLimits());
        try
        {
            vm.LoadDataset(original);
            vm.LoadPlotGroups([new PlotGroup { Signals = ["test"] }]);
            await vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
            var oldCursor = Assert.Single(windows.Cursors);
            oldCursor(.004);
            Assert.Equal(.004, vm.CursorTime);
            oldCursor(double.NaN);
            oldCursor(double.PositiveInfinity);
            Assert.Equal(.004, vm.CursorTime);

            if (transition == "clear") vm.ClearDataset();
            else if (transition == "replace") vm.LoadDataset(replacement);
            else vm.Dispose();
            var expected = vm.CursorTime;
            oldCursor(.012);
            Assert.Equal(expected, vm.CursorTime);

            if (transition != "dispose")
            {
                if (transition == "clear") vm.LoadDataset(replacement);
                vm.LoadPlotGroups([new PlotGroup { Signals = ["test"] }]);
                await vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
                Assert.Equal(2, windows.Cursors.Count);
                windows.Cursors[1](.017);
                Assert.Equal(.017, vm.CursorTime);
                oldCursor(.006);
                Assert.Equal(.017, vm.CursorTime);
            }
        }
        finally
        {
            await vm.CancelAndWaitAsync();
            await vm.ActiveUsage.CancelAndWaitAsync();
        }
    });

    [Fact]
    public Task WindowForwardsSnappedCursorAndBothFlagsIncludingReturningToTheSameLocalPoint() => PlotRegressionTests.OnDispatcher(() =>
    {
        using var dataset = PlotRegressionTests.Dataset(20);
        var panels = new PlotModelBuilder().Build(dataset, [new PlotGroup { Signals = ["test"] }], new());
        foreach (var panel in panels) ((IPlotModel)panel.PlotModel).Update(true);
        var calls = new List<double>();
        var callbackThread = 0;
        var window = new PlotPanelsWindow(panels, new(), new(), false, false, null,
            time => { calls.Add(time); callbackThread = Environment.CurrentManagedThreadId; });
        try
        {
            CursorAction(window, "SetCursorAt", .0024, panels[0]);
            // Returning from another window to the same local point still has to
            // restore the shared cursor, even if this plot needs no new drawing.
            CursorAction(window, "SetCursorAt", .0024, panels[0]);
            CursorAction(window, "SetFlagAAt", .0052, panels[0]);
            CursorAction(window, "SetFlagBAt", .0082, panels[0]);
            Assert.Equal(new[] { .002, .002, .005, .008 }, calls);
            Assert.Equal(Environment.CurrentManagedThreadId, callbackThread);
            CursorAction(window, "SetCursorAt", double.NaN, panels[0]);
            CursorAction(window, "SetFlagAAt", double.PositiveInfinity, panels[0]);
            Assert.Equal(4, calls.Count);
            window.Close();
            CursorAction(window, "SetCursorAt", .010, panels[0]);
            CursorAction(window, "SetFlagBAt", .014, panels[0]);
            Assert.Equal(4, calls.Count);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ClosedWindowReleasesTheCursorCallbackTarget() => PlotRegressionTests.OnDispatcher(() =>
    {
        using var dataset = PlotRegressionTests.Dataset(20);
        var panels = new PlotModelBuilder().Build(dataset, [new PlotGroup { Signals = ["test"] }], new());
        var (window, target) = WindowWithCallbackTarget(panels);
        Assert.True(target.IsAlive);
        window.Close();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(target.IsAlive);
        GC.KeepAlive(window); // The closed window itself must not retain the owner through its callback.
        return Task.CompletedTask;
    });

    private static void CursorAction(PlotPanelsWindow window, string method, double time, PlotPanelModel panel) =>
        typeof(PlotPanelsWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [time, panel]);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PlotPanelsWindow Window, WeakReference Target) WindowWithCallbackTarget(IReadOnlyList<PlotPanelModel> panels)
    {
        var target = new CursorTarget();
        return (new PlotPanelsWindow(panels, new(), new(), false, false, null, target.Set), new WeakReference(target));
    }

    private sealed class CursorTarget
    {
        public double Time { get; private set; }
        public void Set(double time) => Time = time;
    }

    private sealed class RecordingWindows : IPlotWindowService
    {
        public List<Action<double>> Cursors { get; } = [];
        public void ShowPlots(IReadOnlyList<PlotPanelModel> panels, PlotViewOptions renderedOptions, DisplayLimits displayLimits,
            bool linkXAxisAcrossPanels, bool linkYAxisAcrossPanels, DateTimeOffset? startTimeUtc, Action<double>? cursorChanged = null)
        {
            Assert.NotEmpty(panels);
            Assert.NotNull(cursorChanged);
            Cursors.Add(cursorChanged);
        }
    }
}
