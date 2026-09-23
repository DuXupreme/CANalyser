using CanAnalyzer.App.Infrastructure;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class CrashTelemetryJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "canalyser-crash-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ActiveSessionIsNotReportedAndCleanExitDeletesMarker()
    {
        using var journal = new CrashTelemetryJournal(_root);
        journal.Start("active", "unexpected");
        await journal.ReplayAsync(_ => throw new InvalidOperationException("Running session reported"));
        journal.Dispose();
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task CrashIsDurableAndRemovedOnlyAfterAcknowledgement()
    {
        using (var journal = new CrashTelemetryJournal(_root))
        {
            journal.Start("fatal", "unexpected");
            journal.RecordCrash("original crash");
            journal.RecordCrash("duplicate handler");
        }
        using var next = new CrashTelemetryJournal(_root);
        var attempts = new List<string>();
        await next.ReplayAsync(payload => { attempts.Add(payload); return Task.FromResult(false); });
        Assert.Single(Directory.GetFiles(_root));
        await next.ReplayAsync(payload => { attempts.Add(payload); return Task.FromResult(true); });
        Assert.Equal(new[] { "original crash", "original crash" }, attempts);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AbandonedSessionIsReportedWhileOtherInstanceStaysActive()
    {
        Directory.CreateDirectory(_root);
        // A killed process leaves this unlocked file behind.
        File.WriteAllText(Path.Combine(_root, "killed.json"), "unexpected exit");
        using var active = new CrashTelemetryJournal(_root);
        active.Start("running", "still running");
        using var next = new CrashTelemetryJournal(_root);
        var received = new List<string>();
        await next.ReplayAsync(payload => { received.Add(payload); return Task.FromResult(true); });
        Assert.Equal(new[] { "unexpected exit" }, received);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task NetworkExceptionKeepsPendingReport()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "crash.json"), "crash");
        using var journal = new CrashTelemetryJournal(_root);
        await Assert.ThrowsAsync<HttpRequestException>(() => journal.ReplayAsync(_ => throw new HttpRequestException()));
        Assert.Single(Directory.GetFiles(_root));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
