namespace CanAnalyzer.App.Services;

/// <summary>
/// Serializes expensive work and publishes only the latest request. Request and callbacks run
/// on the owning UI context; compute may use a worker. A cancelled worker is awaited before
/// the next request, so callers can safely keep resources alive while IsRunning is true.
/// </summary>
public sealed class LatestWorkQueue<TRequest, TResult>(
    Func<TRequest, CancellationToken, Task<TResult>> compute,
    Action<TRequest, TResult> publish,
    Action<bool> runningChanged,
    Action<Exception> failed)
{
    private (TRequest Request, long Version)? _pending;
    private long _version;
    private Task? _running;
    private CancellationTokenSource? _cancellation;
    public bool IsRunning => _running is not null;

    public Task RequestAsync(TRequest request)
    {
        _pending = (request, ++_version);
        _cancellation?.Cancel();
        if (_running is not null) return _running;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running = completion.Task;
        _ = DrainAsync(completion);
        return completion.Task;
    }

    private async Task DrainAsync(TaskCompletionSource completion)
    {
        try
        {
            runningChanged(true);
            await Task.Yield();
            while (_pending is { } next)
            {
                _pending = null;
                using var cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                try
                {
                    var result = await compute(next.Request, cancellation.Token);
                    if (next.Version == _version && !cancellation.IsCancellationRequested)
                        publish(next.Request, result);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (next.Version == _version) failed(ex);
                }
                finally { _cancellation = null; }
            }
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        finally
        {
            _running = null;
            try { runningChanged(false); }
            catch (Exception ex) { completion.TrySetException(ex); }
            completion.TrySetResult();
        }
    }
}
