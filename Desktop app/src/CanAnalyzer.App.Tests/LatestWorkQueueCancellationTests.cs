using CanAnalyzer.App.Services;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class LatestWorkQueueCancellationTests
{
    [Fact]
    public Task CancelPending_DiscardsActiveAndQueuedResultsButWaitsForWorkerBeforeBecomingIdle() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<int>();
        var published = new List<int>();
        var busy = new List<bool>();
        var errors = new List<Exception>();
        CancellationToken activeToken = default;
        var queue = new LatestWorkQueue<int, int>(async (request, token) =>
        {
            calls.Add(request);
            if (request == 1)
            {
                activeToken = token;
                started.SetResult();
                // Model a worker that cannot abort its current allocation or library call.
                await release.Task;
            }
            return request;
        }, (_, result) => published.Add(result), busy.Add, errors.Add);

        var running = queue.RequestAsync(1);
        await started.Task;
        _ = queue.RequestAsync(2);
        queue.CancelPending();
        Assert.True(activeToken.IsCancellationRequested);
        Assert.True(queue.IsRunning);
        Assert.False(running.IsCompleted);
        Assert.Empty(published);

        release.SetResult();
        await running;
        Assert.False(queue.IsRunning);
        Assert.Equal(new[] { 1 }, calls);
        Assert.Empty(published);
        Assert.Empty(errors);
        Assert.Equal(new[] { true, false }, busy);

        // Cancellation must not poison later explicit requests.
        await queue.RequestAsync(3);
        Assert.Equal(new[] { 3 }, published);
        Assert.Equal(new[] { 1, 3 }, calls);
        Assert.False(queue.IsRunning);
    });
}
