using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.App.Tests;

[Collection("UI activity diagnostics")]
public sealed class DispatcherWatchdogTests
{
    private static DispatcherWatchdogOptions Options(int maximumIncidents = 8) => new()
    {
        StartupGrace = TimeSpan.Zero,
        HangThreshold = TimeSpan.FromSeconds(3),
        MaximumObservationGap = TimeSpan.FromSeconds(5),
        MaximumIncidents = maximumIncidents
    };

    [Fact]
    public void Detector_ReportsOncePerIncidentAndOnceOnRecovery_WithSessionLimit()
    {
        var monitor = new DispatcherResponsivenessMonitor(Options(maximumIncidents: 1));
        Assert.Null(monitor.Observe(TimeSpan.Zero, true));
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(2), false));
        Assert.False(monitor.Observe(TimeSpan.FromSeconds(3), false)!.Recovered);
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(5), false));
        var recovery = monitor.Observe(TimeSpan.FromSeconds(6), true);
        Assert.True(recovery!.Recovered);
        Assert.Equal(TimeSpan.FromSeconds(6), recovery.UnresponsiveFor);
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(7), true));
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(10), false));
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(11), true));
    }

    [Fact]
    public void Detector_IgnoresStartupDebuggerSuspendAndDelayedObservation()
    {
        var monitor = new DispatcherResponsivenessMonitor(Options() with { StartupGrace = TimeSpan.FromSeconds(10) });
        for (var second = 0; second < 12; second++)
            Assert.Null(monitor.Observe(TimeSpan.FromSeconds(second), false));
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(12), false, debuggerAttached: true));
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(14), false));
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(100), false));
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(102), false));
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(103), false, powerStateChanged: true));
        Assert.Null(monitor.Observe(TimeSpan.FromSeconds(105), false));
        Assert.False(monitor.Observe(TimeSpan.FromSeconds(106), false)!.Recovered);
    }

    [Fact]
    public Task BlockedDispatcher_IsReportedOffUiThread_AndRecovers() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var hung = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseUi = new ManualResetEventSlim();
        var reports = new ConcurrentQueue<DispatcherResponsivenessEvent>();
        using var watcher = new DispatcherWatchdog(Dispatcher.CurrentDispatcher, report =>
        {
            reports.Enqueue(report);
            if (report.Recovered) recovered.TrySetResult();
            else
            {
                hung.TrySetResult(Environment.CurrentManagedThreadId);
                releaseUi.Set();
            }
        }, new()
        {
            ProbeInterval = TimeSpan.FromMilliseconds(20),
            HangThreshold = TimeSpan.FromMilliseconds(200),
            StartupGrace = TimeSpan.Zero,
            MaximumObservationGap = TimeSpan.FromSeconds(2)
        });
        // Deliberately block the real WPF thread. The callback itself releases it.
        Assert.True(releaseUi.Wait(TimeSpan.FromSeconds(8)), "The independent watchdog missed the blocked dispatcher.");
        Assert.NotEqual(uiThread, await hung.Task);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watcher.Dispose();
        await watcher.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Collection(reports, report => Assert.False(report.Recovered), report => Assert.True(report.Recovered));
    });

    [Fact]
    public Task Disposal_DoesNotRequireDispatcherToProcessThePendingHeartbeat() => PlotRegressionTests.OnDispatcher(() =>
    {
        using var watcher = new DispatcherWatchdog(Dispatcher.CurrentDispatcher, _ => { }, new()
        {
            ProbeInterval = TimeSpan.FromMilliseconds(10),
            HangThreshold = TimeSpan.FromSeconds(15),
            StartupGrace = TimeSpan.Zero
        });
        Thread.Sleep(50); // Leave a pending heartbeat while the dispatcher is blocked.
        watcher.Dispose();
        Assert.True(watcher.Completion.Wait(TimeSpan.FromSeconds(3)));
        watcher.Dispose();
        return Task.CompletedTask;
    });

    [Fact]
    public async Task DurableQueue_SurvivesNewInstanceBoundsStorageAndDeletesOnlyAfterAcknowledgement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "canalyser-watchdog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var queue = new PendingTelemetryEvents(directory, maximumEvents: 2);
            Assert.True(queue.Record("{\"event_name\":\"app_ui_unresponsive\"}"));
            Assert.True(queue.Record("{\"event_name\":\"app_ui_responsive_again\"}"));
            Assert.False(queue.Record("{}"));
            Assert.False(queue.Record(new string('x', 20_000)));
            await new PendingTelemetryEvents(directory).ReplayAsync((_, _) => Task.FromResult(false));
            Assert.Equal(2, Directory.GetFiles(directory, "*.json").Length);
            var sent = new List<string>();
            await new PendingTelemetryEvents(directory).ReplayAsync((json, _) =>
            {
                sent.Add(json);
                return Task.FromResult(true);
            });
            Assert.Equal(2, sent.Count);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DurableQueue_CancellationKeepsTheUnacknowledgedEvent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "canalyser-watchdog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var queue = new PendingTelemetryEvents(directory);
            queue.Record("{}");
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.ReplayAsync(async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }, cancellation.Token));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Telemetry_RespectsConsentAndPersistsOnlyMinimalHangProperties(bool activeOperation)
    {
        using var activity = activeOperation ? UiActivityDiagnostics.Begin("plot_render") : default;
        var directory = Path.Combine(Path.GetTempPath(), "canalyser-watchdog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var telemetry = new TelemetryService(NullLogger<TelemetryService>.Instance, directory);
            var incident = new DispatcherResponsivenessEvent(false, TimeSpan.FromSeconds(15));
            telemetry.Configure(new() { Enabled = false });
            telemetry.RecordDispatcherResponsiveness(incident);
            Assert.False(Directory.Exists(directory));
            telemetry.Configure(new() { Enabled = true, EndpointUrl = "", InstallationId = "watchdog-test" });
            telemetry.RecordDispatcherResponsiveness(incident);
            var pending = Assert.Single(Directory.GetFiles(Path.Combine(directory, "pending-responsiveness")));
            // Offline replay can briefly hold an exclusive read handle.
            string json = "";
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try { json = await File.ReadAllTextAsync(pending); break; }
                catch (IOException) { await Task.Delay(10); }
            }
            using var document = JsonDocument.Parse(json);
            Assert.Equal("app_ui_unresponsive", document.RootElement.GetProperty("event_name").GetString());
            var properties = document.RootElement.GetProperty("properties");
            var expected = new List<string> { "gen2_collections", "managed_memory_bytes", "source", "unresponsive_ms", "working_set_bytes" };
            if (activeOperation)
            {
                expected.AddRange(["ui_activity", "ui_activity_elapsed_ms"]);
                Assert.Equal("plot_render", properties.GetProperty("ui_activity").GetString());
                Assert.True(properties.GetProperty("ui_activity_elapsed_ms").GetInt64() >= 0);
            }
            Assert.Equal(expected.Order(StringComparer.Ordinal),
                properties.EnumerateObject().Select(property => property.Name));
            Assert.Equal(15000, properties.GetProperty("unresponsive_ms").GetInt64());
            telemetry.Configure(new() { Enabled = false });
            telemetry.RecordDispatcherResponsiveness(incident);
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "pending-responsiveness")));
            telemetry.Dispose();
            // Let the bounded, already queued offline replay release its local file handle.
            await Task.Delay(50);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
