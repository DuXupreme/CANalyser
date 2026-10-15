using CanAnalyzer.App.Infrastructure;
using Xunit;

namespace CanAnalyzer.App.Tests;

[CollectionDefinition("UI activity diagnostics", DisableParallelization = true)]
public sealed class UiActivityDiagnosticsCollection { }

[Collection("UI activity diagnostics")]
public sealed class UiActivityDiagnosticsTests
{
    [Fact]
    public void NestedScopes_RestorePreviousActivityAndLeaveNoStaleSnapshot()
    {
        Assert.Null(UiActivityDiagnostics.Capture());
        using (UiActivityDiagnostics.Begin("plot_render"))
        {
            var outer = UiActivityDiagnostics.Capture()!.Value;
            Assert.Equal("plot_render", outer.Name);
            Assert.True(outer.Elapsed >= TimeSpan.Zero);
            var inner = UiActivityDiagnostics.Begin(UiActivity.ViewportSampling);
            Assert.Equal("viewport_sampling", UiActivityDiagnostics.Capture()!.Value.Name);
            inner.Dispose();
            inner.Dispose();
            Assert.Equal("plot_render", UiActivityDiagnostics.Capture()!.Value.Name);
            Assert.True(UiActivityDiagnostics.Capture()!.Value.Elapsed >= outer.Elapsed);
        }
        Assert.Null(UiActivityDiagnostics.Capture());
        default(UiActivityDiagnostics.Scope).Dispose();
        Assert.Null(UiActivityDiagnostics.Capture());
    }

    [Fact]
    public async Task BackgroundSnapshots_RemainConsistentWhileUiScopesChange()
    {
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() =>
        {
            started.SetResult();
            while (!stop.IsCancellationRequested)
            {
                if (UiActivityDiagnostics.Capture() is not { } snapshot) continue;
                Assert.Contains(snapshot.Name, new[] { "plot_render", "plot_model_publish" });
                Assert.True(snapshot.Elapsed >= TimeSpan.Zero);
            }
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 10_000; i++)
            {
                using var activity = UiActivityDiagnostics.Begin(i % 2 == 0 ? UiActivity.PlotRender : UiActivity.PlotModelPublish);
                Assert.NotNull(UiActivityDiagnostics.Capture());
            }
        }
        finally { stop.Cancel(); }
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(UiActivityDiagnostics.Capture());
    }

    [Fact]
    public void ArbitraryNamesCannotBecomeTelemetryProperties()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UiActivityDiagnostics.Begin("private log name"));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiActivityDiagnostics.Begin(UiActivity.None));
        Assert.Null(UiActivityDiagnostics.Capture());
    }
}
