using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CanAnalyzer.App.Infrastructure;

public sealed record DispatcherWatchdogOptions
{
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan HangThreshold { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan StartupGrace { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumObservationGap { get; init; } = TimeSpan.FromSeconds(5);
    public int MaximumIncidents { get; init; } = 8;
}

public sealed record DispatcherResponsivenessEvent(bool Recovered, TimeSpan UnresponsiveFor);

/// <summary>Deterministic detector; elapsed time is monotonic and never based on the wall clock.</summary>
public sealed class DispatcherResponsivenessMonitor(DispatcherWatchdogOptions options)
{
    private TimeSpan? _previous;
    private TimeSpan _warmupUntil;
    private TimeSpan _pendingSince;
    private bool _reported;
    private int _incidents;

    public DispatcherResponsivenessEvent? Observe(TimeSpan now, bool acknowledged,
        bool debuggerAttached = false, bool powerStateChanged = false)
    {
        if (_previous is null) _warmupUntil = now + options.StartupGrace;
        var gap = _previous is { } previous ? now - previous : TimeSpan.Zero;
        _previous = now;
        if (debuggerAttached || powerStateChanged || gap < TimeSpan.Zero ||
            gap > options.MaximumObservationGap || now < _warmupUntil)
        {
            _pendingSince = now;
            _reported = false;
            return null;
        }

        var duration = now - _pendingSince;
        if (acknowledged)
        {
            var recovery = _reported ? new DispatcherResponsivenessEvent(true, duration) : null;
            _pendingSince = now;
            _reported = false;
            return recovery;
        }
        if (_reported || duration < options.HangThreshold || _incidents >= options.MaximumIncidents) return null;
        _reported = true;
        _incidents++;
        return new(false, duration);
    }
}

/// <summary>
/// One independent background thread and at most one queued UI heartbeat. Reporting never runs
/// on the dispatcher. Start after ContentRendered; disposal never waits for a blocked dispatcher.
/// </summary>
public sealed class DispatcherWatchdog : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<DispatcherResponsivenessEvent> _report;
    private readonly DispatcherWatchdogOptions _options;
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _lifetime = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _powerNotifications;
    private DispatcherOperation? _probe;
    private int _acknowledged = 1;
    private int _powerStateChanged;
    private bool _finished;
    private bool _disposed;

    public DispatcherWatchdog(Dispatcher dispatcher, Action<DispatcherResponsivenessEvent> report,
        DispatcherWatchdogOptions? options = null)
    {
        _dispatcher = dispatcher;
        _report = report;
        _options = options ?? new();
        if (_options.ProbeInterval <= TimeSpan.Zero || _options.HangThreshold <= TimeSpan.Zero ||
            _options.StartupGrace < TimeSpan.Zero || _options.MaximumObservationGap <= _options.ProbeInterval ||
            _options.MaximumIncidents < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _powerNotifications = true;
        }
        catch (InvalidOperationException) { /* The scheduling-gap check also detects suspend/resume. */ }
        _thread = new Thread(Run) { IsBackground = true, Name = "CANalyser UI watchdog" };
        _thread.Start();
    }

    public Task Completion => _completion.Task;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args) =>
        Interlocked.Exchange(ref _powerStateChanged, 1);

    private void Run()
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var monitor = new DispatcherResponsivenessMonitor(_options);
            monitor.Observe(TimeSpan.Zero, acknowledged: true);
            while (!_stop.WaitOne(_options.ProbeInterval))
            {
                if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) break;
                var acknowledged = Volatile.Read(ref _acknowledged) != 0;
                var report = monitor.Observe(clock.Elapsed, acknowledged, Debugger.IsAttached,
                    Interlocked.Exchange(ref _powerStateChanged, 0) != 0);
                if (report is not null)
                {
                    try { _report(report); }
                    catch { /* Diagnostics must not crash the application. */ }
                }
                if (!acknowledged) continue;
                Interlocked.Exchange(ref _acknowledged, 0);
                _probe = _dispatcher.BeginInvoke(DispatcherPriority.Input,
                    new Action(() => Interlocked.Exchange(ref _acknowledged, 1)));
            }
        }
        catch (Exception) { /* Watchdog failures must not terminate the application. */ }
        finally
        {
            try
            {
                _probe?.Abort();
                if (_powerNotifications) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch (InvalidOperationException) { /* Dispatcher/system events are shutting down. */ }
            lock (_lifetime)
            {
                _finished = true;
                _stop.Dispose();
            }
            _completion.TrySetResult();
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_finished) _stop.Set();
        }
    }
}
