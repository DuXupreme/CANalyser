using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class LatencyTelemetryRegressionTests
{
    [Theory]
    [InlineData("import_gap", "data_gap", "overlapt een meetgat")]
    [InlineData("duplicate_time", "duplicate_timestamp", "dubbele tijdstempels")]
    [InlineData("decreasing_time", "non_monotonic_timestamp", "lopen terug")]
    [InlineData("nonfinite_time", "non_finite_time", "niet-eindig tijdstip")]
    [InlineData("nonfinite_value", "non_finite_value", "niet-eindige meetwaarde")]
    [InlineData("invalid_window", "invalid_time_window", "tijdvenster is ongeldig")]
    public Task InvalidLatencyInput_ReportsHandledFailureAndKeepsDispatcherUsable(
        string invalidInput, string expectedReason, string expectedExplanation) => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset(invalidInput);
        using var validData = Dataset();
        var telemetry = new RecordingTelemetry();
        var vm = new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry,
            new ActiveUsageViewModel(telemetry));
        var uiThread = Environment.CurrentManagedThreadId;
        var wrongThread = false;
        vm.PropertyChanged += (_, _) => wrongThread |= Environment.CurrentManagedThreadId != uiThread;

        vm.LoadDataset(data);
        await WaitForIdle(vm);
        if (invalidInput == "invalid_window")
        {
            telemetry.Events.Clear();
            vm.UseJoystickTimeWindow = true;
            vm.JoystickTimeStartSeconds = .4;
            vm.JoystickTimeEndSeconds = .2;
            await vm.RecomputeCommand.ExecuteAsync(null);
        }

        var failure = Assert.Single(telemetry.Events, e => e.Name == "analytics_recompute_failed");
        Assert.Equal("Latency", failure.Properties["analysis_stage"]);
        Assert.Equal(nameof(ArgumentException), failure.Properties["exception_type"]);
        Assert.Equal(expectedReason, failure.Properties["failure_reason"]);
        Assert.Equal(new[] { "analysis_stage", "exception_type", "failure_reason" }, failure.Properties.Keys.Order());
        Assert.DoesNotContain(telemetry.Events, e => e.Name == "analytics_recompute_completed");
        var explanation = Assert.Single(vm.DelayMetrics);
        Assert.Equal("Niet berekend", explanation.Name);
        Assert.Contains(expectedExplanation, explanation.Value);
        Assert.Contains("Niet alle analyses berekend: Latency", vm.StatusText);
        Assert.Equal("Controleer signalen en tijdvenster", vm.DelayOverlayModel.Title);
        Assert.False(vm.IsBusy);
        Assert.True(vm.RecomputeCommand.CanExecute(null));
        Assert.Empty(telemetry.ActiveOperations);
        Assert.False(wrongThread);

        // The CAN stage follows latency in the real recompute pipeline. Its result proves
        // the handled exception did not abort the remaining analyses.
        var can = JsonSerializer.SerializeToElement(vm.CaptureExport()).GetProperty("Can").GetProperty("Result");
        Assert.Equal(2, can.GetProperty("FrameCount").GetInt32());
        var heartbeat = Dispatcher.CurrentDispatcher.InvokeAsync(
            () => Environment.CurrentManagedThreadId, DispatcherPriority.ApplicationIdle);
        Assert.Equal(uiThread, await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // A subsequent valid load can complete; the prior failure does not leave a
        // pending operation, busy flag, or failed-stage state behind.
        telemetry.Events.Clear();
        vm.LoadDataset(validData);
        await WaitForIdle(vm);
        Assert.DoesNotContain(telemetry.Events, e => e.Name == "analytics_recompute_failed");
        Assert.Single(telemetry.Events, e => e.Name == "analytics_recompute_completed");
        Assert.Empty(telemetry.ActiveOperations);
        Assert.False(vm.IsBusy);
    });

    private static async Task WaitForIdle(JoystickAnalyticsViewModel vm)
    {
        var timeout = Stopwatch.StartNew();
        while (vm.IsBusy)
        {
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(10), "The analysis did not leave its busy state.");
            await Task.Delay(5);
        }
    }

    private static CanDataset Dataset(string? invalidInput = null)
    {
        double[] commandTime = [0, .1, .2, .3, .4, .5];
        double[] values = [0, 0, 1, 1, 0, 0];
        if (invalidInput == "duplicate_time") commandTime[2] = commandTime[1];
        if (invalidInput == "decreasing_time") commandTime[2] = .05;
        if (invalidInput == "nonfinite_value") values[2] = double.NaN;
        SignalSeries[] series =
        [
            new("command", commandTime, values),
            new("feedback", [0, .1, .2, .3, .4, .5], [0, 0, 0, 1, 1, 0])
        ];
        // Canonical timestamps are integer nanoseconds. Deliberately corrupt the public
        // projection to exercise the defensive non-finite-time validation as well.
        if (invalidInput == "nonfinite_time") series[0].Time[2] = double.NaN;
        return new()
        {
            RawFrames = [new RawCanFrame(0d, 1, 1, [0], "Rx", "synthetic", false),
                new RawCanFrame(.5d, 1, 1, [1], "Rx", "synthetic", false)],
            DecodedSamples = [],
            MessageSummaries = [],
            SignalLabels = series.Select(s => s.Label).ToArray(),
            SignalSeriesByLabel = series.ToDictionary(s => s.Label),
            SignalSeriesByIdentity = series.ToDictionary(s => s.Identity),
            Diagnostics = new(2, 0, series.Length, 0, 0, ""),
            ImportReport = new("synthetic", 2, 0, 2, 0, [])
            {
                Gaps = invalidInput == "import_gap" ? [new(.25, .35)] : []
            }
        };
    }

    private sealed class RecordingTelemetry : ITelemetryService
    {
        private int _nextOperation;
        public string LocalLogPath => string.Empty;
        public string InstallationId => "synthetic";
        public List<(string Name, IReadOnlyDictionary<string, object?> Properties)> Events { get; } = [];
        public HashSet<string> ActiveOperations { get; } = [];
        public void Configure(TelemetryOptions options) { }
        public Task TrackEventAsync(string eventName, IReadOnlyDictionary<string, object?>? properties = null,
            CancellationToken cancellationToken = default)
        {
            Events.Add((eventName, properties is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(properties)));
            return Task.CompletedTask;
        }
        public string BeginCriticalOperation(string operationName, IReadOnlyDictionary<string, object?>? properties = null)
        {
            var id = $"{operationName}-{++_nextOperation}";
            ActiveOperations.Add(id);
            return id;
        }
        public void CompleteCriticalOperation(string operationId) => ActiveOperations.Remove(operationId);
        public Task ReportInterruptedOperationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
