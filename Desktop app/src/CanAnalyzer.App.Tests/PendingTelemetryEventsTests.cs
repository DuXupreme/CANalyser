using System.IO;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class PendingTelemetryEventsTests
{
    [Fact]
    public async Task Replay_DiscardsTruncatedOversizedAndNonObjectPayloadsWithoutBlockingValidEvents()
    {
        var directory = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "000-truncated.json"), "{\"event_name\":");
            await File.WriteAllTextAsync(Path.Combine(directory, "001-oversized.json"), new string('x', 20_000));
            await File.WriteAllTextAsync(Path.Combine(directory, "002-nonobject.json"), "[]");
            var queue = new PendingTelemetryEvents(directory);
            Assert.True(queue.Record("{\"event_name\":\"app_ui_unresponsive\"}"));
            var sent = new List<string>();
            await queue.ReplayAsync((json, _) =>
            {
                sent.Add(json);
                return Task.FromResult(true);
            });
            Assert.Equal("{\"event_name\":\"app_ui_unresponsive\"}", Assert.Single(sent));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task IncompleteTemporaryPayload_IsNeverReplayed()
    {
        var directory = CreateDirectory();
        try
        {
            var temporaryPath = Path.Combine(directory, "interrupted.tmp");
            await File.WriteAllTextAsync(temporaryPath, "{\"event_name\":");
            var queue = new PendingTelemetryEvents(directory);
            Assert.True(queue.Record("{}"));
            await queue.ReplayAsync((json, _) =>
            {
                Assert.Equal("{}", json);
                return Task.FromResult(true);
            });
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TemporaryCleanup_DoesNotRemoveAWriterBeforePublication()
    {
        var directory = CreateDirectory();
        try
        {
            var temporaryPath = Path.Combine(directory, "active.tmp");
            var queue = new PendingTelemetryEvents(directory);
            using (var writer = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Delete))
            {
                await queue.ReplayAsync((_, _) => throw new InvalidOperationException("An unpublished file was replayed."));
                Assert.True(File.Exists(temporaryPath));
                // Match production: publish while retaining the writer's handle.
                writer.Write(System.Text.Encoding.UTF8.GetBytes("{}"));
                writer.Flush(flushToDisk: true);
                File.Move(temporaryPath, Path.Combine(directory, "published.json"));
            }
            await queue.ReplayAsync((json, _) => Task.FromResult(json == "{}"));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FullQueue_StillRetriesPersistedEventsWhenANewIncidentIsReported()
    {
        var directory = CreateDirectory();
        try
        {
            // Prevent the unrelated crash-journal startup replay from racing this test.
            // Watchdog recording must work even when that older journal is unavailable.
            await File.WriteAllTextAsync(Path.Combine(directory, "pending-crashes"), "unavailable");
            using var telemetry = new TelemetryService(NullLogger<TelemetryService>.Instance, directory);
            telemetry.Configure(new() { Enabled = true, EndpointUrl = "", InstallationId = "full-queue-test" });
            var pending = Path.Combine(directory, "pending-responsiveness");
            var queue = new PendingTelemetryEvents(pending);
            for (var i = 0; i < 32; i++) Assert.True(queue.Record("{\"event_name\":\"app_ui_unresponsive\"}"));
            telemetry.RecordDispatcherResponsiveness(new(false, TimeSpan.FromSeconds(15)));
            // Even offline replay appends to the local log before an upload attempt. This
            // proves the full-queue path actually schedules replay without network access.
            for (var attempt = 0; attempt < 100 && !File.Exists(telemetry.LocalLogPath); attempt++)
                await Task.Delay(20);
            Assert.True(File.Exists(telemetry.LocalLogPath), "A full pending queue prevented the replay attempt.");
            Assert.Equal(32, Directory.GetFiles(pending, "*.json").Length);
            telemetry.Dispose();
            // Acquire the queue's exclusive replay handles before cleaning up its directory.
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    foreach (var path in Directory.GetFiles(pending))
                        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    break;
                }
                catch (IOException) { await Task.Delay(20); }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "canalyser-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
