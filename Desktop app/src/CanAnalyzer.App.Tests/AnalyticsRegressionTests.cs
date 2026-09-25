using System.Reflection;
using System.Text.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class AnalyticsRegressionTests
{
    internal sealed class Telemetry : ITelemetryService
    {
        public string LocalLogPath => "";
        public string InstallationId => "test";
        public List<string> Events { get; } = [];
        public void Configure(TelemetryOptions options) { }
        public Task TrackEventAsync(string eventName, IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default)
        { Events.Add(eventName); return Task.CompletedTask; }
        public string BeginCriticalOperation(string operationName, IReadOnlyDictionary<string, object?>? properties = null) => operationName;
        public void CompleteCriticalOperation(string operationId) { }
        public Task ReportInterruptedOperationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static CanDataset Dataset(SignalSeries[] series, RawCanFrame[]? frames = null) => new()
    {
        RawFrames = frames ?? [], DecodedSamples = [], MessageSummaries = [],
        SignalLabels = series.Select(s => s.Label).ToArray(),
        SignalSeriesByLabel = series.ToDictionary(s => s.Label), SignalSeriesByIdentity = series.ToDictionary(s => s.Identity),
        Diagnostics = new(0, 0, series.Length, 0, 0, "")
    };

    private static async Task WaitForIdle(JoystickAnalyticsViewModel vm)
    { while (vm.IsBusy) await Task.Delay(5); }

    [Fact]
    public Task LazyAnalysisReads_RunOffDispatcherAndDiscardOldDataset() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "joystick", "pos_x");
        var lazy = new SignalSeries(identity, () =>
        {
            Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
            started.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return ([0L, 100_000_000L, 200_000_000L], new[] { 0d, 0.5, 1d });
        });
        using var old = Dataset([lazy]); using var next = PlotRegressionTests.Dataset(50);
        var telemetry = new Telemetry();
        var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
        var wrongThread = false;
        vm.PropertyChanged += (_, _) => wrongThread |= Environment.CurrentManagedThreadId != uiThread;
        vm.LoadDataset(old);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.LoadDataset(next); old.Dispose();
        Assert.Throws<ObjectDisposedException>(() => old.AcquireReadLease());
        release.Set();
        await WaitForIdle(vm);
        Assert.False(wrongThread);
        Assert.Equal(next.SignalLabels, vm.AvailableSignals);
        Assert.Equal(1, telemetry.Events.Count(e => e == "analytics_recompute_completed"));
        Assert.DoesNotContain("analytics_recompute_failed", telemetry.Events);
    });

    [Fact]
    public Task BackgroundCanStatistics_PreserveChannelCountsAndOriginalTimestampOrder() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset([new SignalSeries("test", [0, 1, 2], [0, 1, 0])],
        [new RawCanFrame(0d, 1, 1, [0], "Rx", "A", false),
         new RawCanFrame(2d, 1, 1, [0], "Rx", "A", false),
         new RawCanFrame(1d, 1, 1, [0], "Rx", "A", false),
         new RawCanFrame(9d, 2, 1, [0], "Rx", "B", true)]);
        var telemetry = new Telemetry();
        var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
        vm.LoadDataset(data); await WaitForIdle(vm);
        var can = JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Can").GetProperty("Result");
        Assert.Equal("A", can.GetProperty("Channel").GetString());
        Assert.Equal(3, can.GetProperty("FrameCount").GetInt32());
        Assert.Equal(2, can.GetProperty("DurationSeconds").GetDouble());
        Assert.Equal(1, can.GetProperty("Streams")[0].GetProperty("NegativeCycleCount").GetInt32());
    });

    [Fact]
    public Task FailedStage_DoesNotEmitSuccessfulRecompute() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var broken = new SignalSeries(new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "joystick", "pos_x"),
            () => throw new InvalidOperationException("Synthetic read failure"));
        using var data = Dataset([broken]);
        var telemetry = new Telemetry();
        var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry));
        vm.LoadDataset(data); await WaitForIdle(vm);
        Assert.Contains("analytics_recompute_failed", telemetry.Events);
        Assert.DoesNotContain("analytics_recompute_completed", telemetry.Events);
        Assert.Contains("Niet alle analyses", vm.StatusText);
    });

    [Fact]
    public Task ActiveUsage_SampledDiskReadRunsOffDispatcherAndSettingsInvalidateResult() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var series = new SignalSeries(new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "motor", "RPM"), 300_000,
            () => throw new InvalidOperationException("Full materialization is not allowed"), count =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                started.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return ([0L, 1_000_000_000L, 2_000_000_000L], new[] { 200d, 200d, 0d });
            });
        using var data = Dataset([series]);
        var vm = new ActiveUsageViewModel(new Telemetry()); vm.LoadDataset(data);
        var task = vm.CalculateCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Threshold = "500"; data.Dispose(); release.Set();
        await task;
        Assert.False(vm.IsBusy); Assert.False(vm.HasResult); Assert.Null(vm.ExportResult);
    });
}
