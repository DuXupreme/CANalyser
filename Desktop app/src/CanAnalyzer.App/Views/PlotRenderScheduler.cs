using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace CanAnalyzer.App.Views;

/// <summary>One bounded redraw queue per UI dispatcher; input has priority over this work.</summary>
internal sealed class PlotRenderScheduler
{
    private static readonly ConditionalWeakTable<Dispatcher, PlotRenderScheduler> Schedulers = new();
    private readonly LinkedList<CachedPlotView> _pending = [];
    private readonly Dictionary<CachedPlotView, LinkedListNode<CachedPlotView>> _nodes = [];
    private readonly DispatcherTimer _timer;

    private PlotRenderScheduler(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    public static PlotRenderScheduler For(Dispatcher dispatcher) => Schedulers.GetValue(dispatcher, d => new(d));

    public void Request(CachedPlotView view)
    {
        if (!_nodes.ContainsKey(view)) _nodes.Add(view, _pending.AddLast(view));
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Remove(CachedPlotView view)
    {
        if (_nodes.Remove(view, out var node)) _pending.Remove(node);
        if (_pending.Count == 0) _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        var started = Stopwatch.GetTimestamp();
        // A single plot may exceed the budget; yield to input before processing
        // another one. Reentrant invalidations belong to the next tick.
        var remaining = _pending.Count;
        try
        {
            while (remaining-- > 0 && _pending.First is { } node)
            {
                _pending.RemoveFirst(); _nodes.Remove(node.Value);
                node.Value.FlushScheduledRender();
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) break;
            }
        }
        finally
        {
            if (_pending.Count > 0)
            {
                // Include work already spent in this frame instead of adding a
                // fresh 16 ms delay after every render batch.
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1,
                    16 - Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                _timer.Start();
            }
            else _timer.Interval = TimeSpan.FromMilliseconds(16);
        }
    }
}
