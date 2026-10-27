using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CanAnalyzer.App.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace CanAnalyzer.App.Services;

/// <summary>Bounded viewport work; only the latest result reaches the owning UI dispatcher.
/// Unattached models retain synchronous sampling for headless rendering and export.</summary>
public sealed class ViewportSampling
{
    private static readonly ConditionalWeakTable<PlotModel, ViewportSampling> Bindings = new();
    private static readonly SemaphoreSlim Workers = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
    private readonly PlotModel _model;
    private readonly Axis _axis;
    private readonly List<Trace> _traces = [];
    private readonly LatestWorkQueue<Request, PlotSeriesRenderer.PointBuffer[]> _work;
    private int _maximumPoints;
    private readonly PlotViewport? _rawViewport;
    private bool _rawActive, _suspended;
    private WeakReference<IPlotView>? _suspendedView;
    private Request? _requested;

    private sealed class Trace(RenderedSeriesData source, Series series, PlotSeriesRenderer.PreparedSeries prepared)
    {
        public RenderedSeriesData Source { get; } = source;
        public Series Series { get; } = series;
        public PlotSeriesRenderer.PointBuffer Full { get; } = prepared.Overview;
        public PlotSeriesRenderer.PointBuffer? Raw { get; set; } = prepared.Raw;
        public PlotViewport? RawViewport { get; } = prepared.RawViewport;
        public PlotSeriesRenderer.PointBuffer? Visible { get; set; }
        public double Minimum { get; set; } = double.NaN;
        public double Maximum { get; set; } = double.NaN;
        public int Budget { get; set; }
        public int Level { get; set; }
    }

    // Immutable snapshots: worker code never reads live Series, axes or a WPF view.
    private sealed record Item(int Index, RenderedSeriesData Source, bool Markers,
        PlotSeriesRenderer.PointBuffer Full, int Budget, int Level);
    private sealed record Request(double Minimum, double Maximum, double ViewMinimum, double ViewMaximum,
        int Budget, IPlotView View, Item[] Items);

    public static void Attach(PlotModel model, IReadOnlyList<RenderedSeriesData> sources, bool enabled,
        int maximumPoints, PlotViewport? rawViewport = null)
    {
        if (Bindings.TryGetValue(model, out var old)) { old.Detach(); Bindings.Remove(model); }
        var axis = model.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
        if (axis is not null) Bindings.Add(model, new(model, axis, sources, enabled, maximumPoints, rawViewport));
    }

    public static void Suspend(PlotModel model, IPlotView? view = null)
    {
        if (!Bindings.TryGetValue(model, out var binding)) return;
        binding._suspended = true;
        // A removed model is already detached when WPF reports the property change.
        // Remember its former owner without retaining that owner's visual tree.
        var owner = view ?? model.PlotView;
        binding._suspendedView = owner is not null ? new(owner) : null;
        binding.Cancel();
    }

    public static void Resume(PlotModel model)
    {
        if (!Bindings.TryGetValue(model, out var binding) || !binding._suspended) return;
        binding._suspended = false;
        binding._suspendedView = null;
        model.InvalidatePlot(false);
    }

    /// <summary>Immediately retires exact viewport rendering before an asynchronous rebuild.</summary>
    public static void ResumeDownsampling(PlotModel model, int? maximumPoints = null)
    {
        if (!Bindings.TryGetValue(model, out var binding)) return;
        binding.Cancel();
        if (maximumPoints.HasValue) binding._maximumPoints = Math.Clamp(maximumPoints.Value, 200, 200_000);
        binding._rawActive = false;
        foreach (var trace in binding._traces)
        {
            trace.Visible = null;
            trace.Full.Apply(trace.Series);
            binding.ReleaseRaw(trace);
        }
        model.InvalidatePlot(false);
    }

    private ViewportSampling(PlotModel model, Axis axis, IReadOnlyList<RenderedSeriesData> sources,
        bool enabled, int maximumPoints, PlotViewport? rawViewport)
    {
        _model = model; _axis = axis; _maximumPoints = Math.Clamp(maximumPoints, 200, 200_000);
        _rawViewport = !enabled && rawViewport is { IsValid: true } ? rawViewport : null;
        _rawActive = _rawViewport.HasValue;
        foreach (var (source, series) in sources.Zip(model.Series))
        {
            if (series is not (LineSeries or ScatterSeries)) continue;
            _traces.Add(new(source, series, PlotSeriesRenderer.GetPrepared(source, series, _maximumPoints)));
        }
        _work = new(ComputeAsync, Publish, _ => { }, ex =>
        {
            _requested = null;
            System.Diagnostics.Trace.TraceError($"Viewport sampling failed: {ex}");
        });
#pragma warning disable CS0618
        model.Updating += BeforeUpdate;
        model.Updated += AfterUpdate;
#pragma warning restore CS0618
    }

    private void Cancel() { _requested = null; _work.CancelPending(); }
    private void Detach()
    {
        _suspended = true; Cancel();
#pragma warning disable CS0618
        _model.Updating -= BeforeUpdate;
        _model.Updated -= AfterUpdate;
#pragma warning restore CS0618
    }

    private void BeforeUpdate(object? sender, EventArgs args)
    {
        // Production series expose full-domain bounds independently of displayed points.
        // Ordinary caller-created OxyPlot series retain the original overview fallback.
        foreach (var trace in _traces)
            if (trace.Series.IsVisible && trace.Series is not IOverviewSeries) trace.Full.Apply(trace.Series);
    }

    private void AfterUpdate(object? sender, EventArgs args)
    {
        // Suspension belongs to the unloaded/removed view. A detached exporter
        // samples synchronously; a different view may start its own worker.
        if (_suspended && _model.PlotView is { } view)
        {
            if (_suspendedView is not null && _suspendedView.TryGetTarget(out var owner) &&
                ReferenceEquals(view, owner)) return;
            _suspended = false;
            _suspendedView = null;
        }
        using var activity = CanAnalyzer.App.Infrastructure.UiActivityDiagnostics.Begin(
            CanAnalyzer.App.Infrastructure.UiActivity.ViewportSampling);
        var minimum = _axis.ActualMinimum; var maximum = _axis.ActualMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum) return;
        if (_rawActive && _rawViewport == new PlotViewport(minimum, maximum))
        {
            foreach (var trace in _traces)
                if (trace.Series.IsVisible)
                    (trace.Raw is not null && trace.RawViewport == _rawViewport ? trace.Raw : trace.Full).Apply(trace.Series);
            return;
        }
        if (_rawActive)
        {
            _rawActive = false;
            foreach (var trace in _traces) { trace.Full.Apply(trace.Series); ReleaseRaw(trace); }
        }

        var budget = _model.PlotArea.Width > 0
            ? Math.Clamp((int)Math.Ceiling(_model.PlotArea.Width), 200, _maximumPoints) : _maximumPoints;
        var dispatcher = (_model.PlotView as DispatcherObject)?.Dispatcher;
        var asynchronous = dispatcher is not null && !dispatcher.HasShutdownStarted;
        var missing = new List<Item>();
        for (var i = 0; i < _traces.Count; i++)
        {
            var trace = _traces[i];
            if (!trace.Series.IsVisible) { trace.Visible = null; continue; }
            var span = trace.Source.Time.Length > 1 ? trace.Source.Time[^1] - trace.Source.Time[0] : 0;
            var fraction = span > 0 ? Math.Clamp((maximum - minimum) / span, double.Epsilon, 1) : 1;
            var level = double.IsFinite(1 / fraction) ? (int)Math.Floor(Math.Log2(1 / fraction)) : 0;
            var density = fraction * Math.Pow(2, level);
            var traceBudget = double.IsFinite(density) ? Math.Max(8, (int)Math.Ceiling(budget * density)) : budget;
            var cached = trace.Visible is not null && trace.Budget == budget && trace.Level == level &&
                (asynchronous ? minimum >= trace.Minimum && maximum <= trace.Maximum :
                    minimum == trace.Minimum && maximum == trace.Maximum);
            if (cached) trace.Visible!.Apply(trace.Series);
            else if (!asynchronous)
            {
                Cancel();
                trace.Visible = Sample(trace.Source, trace.Series is ScatterSeries, trace.Full,
                    minimum, maximum, traceBudget, false, CancellationToken.None);
                trace.Minimum = minimum; trace.Maximum = maximum; trace.Budget = budget; trace.Level = level;
                trace.Visible.Apply(trace.Series);
            }
            else
            {
                // A bounded overview remains available immediately during a large jump.
                // Small pans keep the cached margin until its replacement arrives.
                (trace.Visible is not null && minimum >= trace.Minimum && maximum <= trace.Maximum
                    ? trace.Visible : trace.Full).Apply(trace.Series);
                missing.Add(new(i, trace.Source, trace.Series is ScatterSeries, trace.Full,
                    Math.Max(8, (int)Math.Ceiling(traceBudget * 1.5)), level));
            }
        }
        if (!asynchronous) return;
        if (missing.Count == 0) { Cancel(); return; }
        if (_requested is { } pending && pending.ViewMinimum == minimum && pending.ViewMaximum == maximum &&
            pending.Budget == budget && ReferenceEquals(pending.View, _model.PlotView) && pending.Items.Select(x => x.Index).SequenceEqual(missing.Select(x => x.Index))) return;
        var margin = (maximum - minimum) * 0.25;
        var lo = minimum - margin; var hi = maximum + margin;
        if (!double.IsFinite(lo) || !double.IsFinite(hi)) { lo = minimum; hi = maximum; }
        _requested = new(lo, hi, minimum, maximum, budget, _model.PlotView!, missing.ToArray());
        // Explicitly capture the owning dispatcher, including callers invoking Update directly.
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher!, DispatcherPriority.Background));
            _ = _work.RequestAsync(_requested);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static Task<PlotSeriesRenderer.PointBuffer[]> ComputeAsync(Request request, CancellationToken token)
        => Task.Run(async () =>
        {
            await Workers.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var result = new PlotSeriesRenderer.PointBuffer[request.Items.Length];
                for (var i = 0; i < result.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = request.Items[i];
                    result[i] = Sample(item.Source, item.Markers, item.Full, request.Minimum, request.Maximum,
                        item.Budget, true, token);
                }
                return result;
            }
            finally { Workers.Release(); }
        }, token);

    private static PlotSeriesRenderer.PointBuffer Sample(RenderedSeriesData source, bool markers,
        PlotSeriesRenderer.PointBuffer full, double minimum, double maximum, int budget, bool stable, CancellationToken token)
    {
        var index = source.GetRangeIndex(token);
        if (source.Time.Length == 0 || !index.SupportsRangeQueries) return full;
        var (x, y) = stable ? index.SelectStable(minimum, maximum, budget, token) : index.Select(minimum, maximum, budget, token);
        return PlotSeriesRenderer.CreatePoints(source, x, y, markers, token);
    }

    private void Publish(Request request, PlotSeriesRenderer.PointBuffer[] result)
    {
        if (ReferenceEquals(_requested, request)) _requested = null;
        if (_suspended || !ReferenceEquals(_model.PlotView, request.View) ||
            _axis.ActualMinimum != request.ViewMinimum || _axis.ActualMaximum != request.ViewMaximum) return;
        _requested = null;
        for (var i = 0; i < result.Length; i++)
        {
            var item = request.Items[i]; var trace = _traces[item.Index];
            if (!trace.Series.IsVisible) continue;
            trace.Visible = result[i]; trace.Minimum = request.Minimum; trace.Maximum = request.Maximum;
            trace.Budget = request.Budget; trace.Level = item.Level;
            result[i].Apply(trace.Series);
        }
        _model.InvalidatePlot(false);
    }

    private void ReleaseRaw(Trace trace)
    {
        trace.Raw = null; PlotSeriesRenderer.ReleaseRaw(trace.Series);
        var capacity = Math.Max(_maximumPoints * 2, trace.Full.Count);
        if (trace.Series is LineSeries line)
        {
            line.Decimator = Decimator.Decimate;
            if (line.Points.Capacity > capacity) line.Points.Capacity = Math.Max(capacity, line.Points.Count);
        }
        else if (trace.Series is ScatterSeries scatter && scatter.Points.Capacity > capacity)
            scatter.Points.Capacity = Math.Max(capacity, scatter.Points.Count);
    }
}
