using System.Collections;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class DetachedOpeningCancellationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task CancelWaitsForWorkerAndAllowsSubsequentOpening(bool throughCommand) => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset();
        using var gate = new BuildGate(observeCancellation: true);
        var builder = new BlockingBuilder(gate);
        var window = new RecordingWindow();
        using var vm = Create(builder, window);
        Load(vm, data);
        var status = vm.PresetStatus;
        var opening = vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(gate.Token.CanBeCanceled);
            Assert.True(vm.OpenPlotsInWindowCommand.CanBeCanceled);
            if (throughCommand) vm.OpenPlotsInWindowCommand.Cancel();
            else vm.CancelPlotBuilds();
            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsBusy);
            Assert.False(opening.IsCompleted);
            Assert.Equal(0, window.OpenCount);
        }
        finally { gate.Release.Set(); }
        await opening;
        Assert.False(vm.IsBusy);
        Assert.Equal(status, vm.PresetStatus);
        Assert.Equal(0, window.OpenCount);
        await vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
        Assert.Equal(1, window.OpenCount);
        Assert.Equal(Environment.CurrentManagedThreadId, window.ThreadId);
        Assert.False(vm.IsBusy);
    });

    [Fact]
    public Task DatasetReplacementCancelsOpeningAndRetainsOldStoresUntilWorkerExits() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var gate = new BuildGate(observeCancellation: false);
        var store = new TrackingFrames();
        using var data = DatasetWithStore(store);
        using var replacement = PlotRegressionTests.Dataset();
        var window = new RecordingWindow();
        using var vm = Create(new BlockingBuilder(gate), window);
        Load(vm, data);
        var opening = vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.LoadDataset(replacement);
            data.Dispose();
            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(store.Disposed);
            Assert.True(vm.IsBusy);
            Assert.False(opening.IsCompleted);
        }
        finally { gate.Release.Set(); }
        await opening;
        Assert.True(store.Disposed);
        Assert.Equal(0, window.OpenCount);
        Assert.False(vm.IsBusy);
        Assert.StartsWith("Dataset geladen.", vm.PresetStatus);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ReplacedOptionsOrGroupsCancelTheCapturedOpening(bool replaceGroups) => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset();
        using var gate = new BuildGate(observeCancellation: false);
        var window = new RecordingWindow();
        using var vm = Create(new BlockingBuilder(gate), window);
        Load(vm, data);
        var opening = vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (replaceGroups) vm.LoadPlotGroups([new PlotGroup { Title = "new", Signals = ["test"] }]);
            else vm.ApplyViewOptions(new PlotViewOptions { StepPlot = true });
            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("original", gate.Groups![0].Title);
            Assert.False(gate.Options!.StepPlot);
        }
        finally { gate.Release.Set(); }
        await opening;
        Assert.Equal(0, window.OpenCount);
        Assert.False(vm.IsBusy);
        Assert.DoesNotContain("mislukt", vm.PresetStatus);
    });

    [Fact]
    public Task CancelledOpeningDoesNotClearBusyWhileANewerMainBuildRuns() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset();
        using var detachedGate = new BuildGate(observeCancellation: false);
        using var mainGate = new BuildGate(observeCancellation: true);
        var window = new RecordingWindow();
        using var vm = Create(new BlockingBuilder(detachedGate, mainGate), window);
        Load(vm, data);
        var opening = vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
        Task? rebuilding = null;
        try
        {
            await detachedGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            rebuilding = PlotRegressionTests.Build(vm);
            await mainGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(detachedGate.Token.IsCancellationRequested);
            detachedGate.Release.Set();
            await opening;
            Assert.True(vm.IsBusy);
            Assert.False(rebuilding.IsCompleted);
            Assert.Equal(0, window.OpenCount);
        }
        finally { detachedGate.Release.Set(); mainGate.Release.Set(); }
        if (rebuilding is not null) await rebuilding;
        Assert.False(vm.IsBusy);
        Assert.Single(vm.PlotPanels);
    });

    [Fact]
    public Task DisposingCancelsOpeningWithoutPublishingOrSubscribingToFurtherChanges() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset();
        using var gate = new BuildGate(observeCancellation: false);
        var builder = new BlockingBuilder(gate);
        var window = new RecordingWindow();
        using var vm = Create(builder, window);
        Load(vm, data);
        var opening = vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Dispose();
            vm.DisplayLimits.MaxPointsPerTrace = 200;
            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { gate.Release.Set(); }
        await opening;
        await vm.OpenPlotsInWindowCommand.ExecuteAsync(null);
        Assert.Equal(1, builder.CallCount);
        Assert.Equal(0, window.OpenCount);
        Assert.False(vm.IsBusy);
        Assert.DoesNotContain("mislukt", vm.PresetStatus);
    });

    [Fact]
    public Task CancelledApplyDoesNotStartAutomaticDetachedOpening() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = PlotRegressionTests.Dataset();
        using var gate = new BuildGate(observeCancellation: true);
        var builder = new BlockingBuilder(gate);
        var window = new RecordingWindow();
        using var vm = Create(builder, window);
        Load(vm, data);
        vm.AutoOpenDetachedOnApply = true;
        var applying = vm.ApplyGroupsCommand.ExecuteAsync(null);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.CancelPlotBuilds();
            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { gate.Release.Set(); }
        await applying;
        Assert.Equal(1, builder.CallCount);
        Assert.Equal(0, window.OpenCount);
        Assert.Empty(vm.PlotPanels);
        Assert.False(vm.IsBusy);
    });

    private static AnalysisViewModel Create(IPlotModelBuilder builder, RecordingWindow window)
    {
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        return new(builder, null!, window, null!, new XAxisSyncService(), telemetry,
            new ActiveUsageViewModel(telemetry), NullLogger<AnalysisViewModel>.Instance, new DisplayLimits());
    }

    private static void Load(AnalysisViewModel vm, CanDataset data)
    {
        vm.LoadDataset(data);
        vm.LoadPlotGroups([new PlotGroup { Title = "original", Signals = ["test"] }]);
    }

    private sealed class BuildGate(bool observeCancellation) : IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public CancellationToken Token { get; private set; }
        public IReadOnlyList<PlotGroup>? Groups { get; private set; }
        public PlotViewOptions? Options { get; private set; }

        public void Wait(CancellationToken token, IReadOnlyList<PlotGroup> groups, PlotViewOptions options)
        {
            Token = token; Groups = groups; Options = options;
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release plot builder.");
            if (observeCancellation) token.ThrowIfCancellationRequested();
        }

        public void Dispose() => Release.Dispose();
    }

    private sealed class BlockingBuilder(params BuildGate[] gates) : IPlotModelBuilder
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public IReadOnlyList<PlotPanelModel> Build(CanDataset dataset, IReadOnlyList<PlotGroup> groups,
            PlotViewOptions options, IReadOnlyList<PlotViewport?>? viewports = null)
            => throw new InvalidOperationException("The initial detached opening must pass a cancellation token.");

        public IReadOnlyList<PlotPanelModel> Build(CanDataset dataset, IReadOnlyList<PlotGroup> groups,
            PlotViewOptions options, IReadOnlyList<PlotViewport?>? viewports, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            if (index < gates.Length) gates[index].Wait(cancellationToken, groups, options);
            // Some gates deliberately mimic a library call that returns despite cancellation.
            return new PlotModelBuilder().Build(dataset, groups, options, viewports);
        }
    }

    private sealed class RecordingWindow : IPlotWindowService
    {
        public int OpenCount { get; private set; }
        public int ThreadId { get; private set; }
        public void ShowPlots(IReadOnlyList<PlotPanelModel> panels, PlotViewOptions renderedOptions,
            DisplayLimits displayLimits, bool linkXAxisAcrossPanels, bool linkYAxisAcrossPanels, DateTimeOffset? startTimeUtc,
            Action<double>? cursorChanged = null)
        {
            Assert.Single(panels);
            OpenCount++;
            ThreadId = Environment.CurrentManagedThreadId;
        }
    }

    private static CanDataset DatasetWithStore(TrackingFrames store)
    {
        var series = new SignalSeries("test", [0, 1], [0, 1]);
        return new CanDataset
        {
            RawFrames = store, DecodedSamples = [], MessageSummaries = [], SignalLabels = [series.Label],
            SignalSeriesByLabel = new Dictionary<string, SignalSeries> { [series.Label] = series },
            SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries> { [series.Identity] = series },
            Diagnostics = new(0, 0, 1, 0, 0, "")
        };
    }

    private sealed class TrackingFrames : IReadOnlyList<RawCanFrame>, IDisposable
    {
        public bool Disposed { get; private set; }
        public int Count => 0;
        public RawCanFrame this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<RawCanFrame> GetEnumerator() => Enumerable.Empty<RawCanFrame>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Disposed = true;
    }
}
