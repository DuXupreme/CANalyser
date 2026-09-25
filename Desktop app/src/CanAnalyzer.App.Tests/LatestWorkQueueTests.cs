using CanAnalyzer.App.Services;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class LatestWorkQueueTests
{
    [Fact]
    public Task Queue_HandlesReentrantRequestFromBusyCallback() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var results = new List<int>(); LatestWorkQueue<int, int>? queue = null;
        queue = new((n, _) => Task.FromResult(n), (_, n) => results.Add(n),
            busy => { if (busy) _ = queue!.RequestAsync(2); }, ex => throw ex);
        await queue.RequestAsync(1);
        Assert.Equal(new[] { 2 }, results); Assert.False(queue.IsRunning);
    });

    [Fact]
    public Task Queue_ReportsPublishingFailureAndReleasesBusyState() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var errors = new List<Exception>(); var busy = new List<bool>();
        var queue = new LatestWorkQueue<int, int>((n, _) => Task.FromResult(n),
            (_, _) => throw new InvalidOperationException("publish"), busy.Add, errors.Add);
        await queue.RequestAsync(1);
        Assert.Single(errors); Assert.Equal("publish", errors[0].Message);
        Assert.False(queue.IsRunning); Assert.Equal(new[] { true, false }, busy);
    });
    [Fact]
    public Task Queue_DiscardsStaleWorkSerializesWorkersAndKeepsUiResponsive() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var start = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var published = new List<int>(); var busy = new List<bool>(); var active = 0; var maxActive = 0;
        var queue = new LatestWorkQueue<int, int>(async (n, token) =>
        {
            active++; maxActive = Math.Max(active, maxActive);
            if (n == 1) { start.SetResult(); await release.Task; }
            await Task.Delay(20); // Deliberately ignore cancellation to model non-cancellable libraries.
            active--; return n;
        }, (_, n) => published.Add(n), busy.Add, ex => throw ex);
        var first = queue.RequestAsync(1); await start.Task;
        var second = queue.RequestAsync(2); var last = queue.RequestAsync(3);
        Assert.True(queue.IsRunning); Assert.Same(first, second); Assert.Same(first, last);
        await Task.Delay(10); Assert.Empty(published);
        release.SetResult(); await last;
        Assert.Equal(new[] { 3 }, published); Assert.Equal(1, maxActive);
        Assert.Equal(new[] { true, false }, busy); Assert.False(queue.IsRunning);
    });

    [Fact]
    public Task Queue_RecoversFromFailureAndImmediateCompletion() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var errors = new List<Exception>(); var results = new List<int>();
        var queue = new LatestWorkQueue<int, int>((n, _) => n == 1
            ? Task.FromException<int>(new InvalidOperationException("fixture")) : Task.FromResult(n),
            (_, n) => results.Add(n), _ => { }, errors.Add);
        await queue.RequestAsync(1); Assert.Single(errors); Assert.False(queue.IsRunning);
        await queue.RequestAsync(2); Assert.Equal(new[] { 2 }, results); Assert.False(queue.IsRunning);
    });
}
