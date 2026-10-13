using System.Runtime.CompilerServices;
using CanAnalyzer.App.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace CanAnalyzer.App.Services;

/// <summary>
/// Uses bounded full-range points for axis updates and visible points for rendering. Exact
/// viewport points are prepared on a worker; navigation immediately returns to sampled data.
/// </summary>
public sealed class ViewportSampling
{
    private static readonly ConditionalWeakTable<PlotModel, ViewportSampling> Bindings = new();
    private readonly PlotModel _model;
    private readonly Axis _axis;
    private readonly List<Trace> _traces = [];
    private int _maximumPoints;
    private readonly PlotViewport? _rawViewport;
    private bool _rawActive;
    private double _minimum = double.NaN, _maximum = double.NaN;
    private int _budget;

    private sealed class Trace(RenderedSeriesData source, Series series, PlotSeriesRenderer.PreparedSeries prepared)
    {
        public RenderedSeriesData Source { get; } = source;
        public Series Series { get; } = series;
        public PlotSeriesRenderer.PointBuffer Full { get; } = prepared.Overview;
        public PlotSeriesRenderer.PointBuffer? Raw { get; set; } = prepared.Raw;
        public PlotViewport? RawViewport { get; } = prepared.RawViewport;
        public PlotSeriesRenderer.PointBuffer? Visible { get; set; }
    }

    public static void Attach(PlotModel model, IReadOnlyList<RenderedSeriesData> sources, bool enabled,
        int maximumPoints, PlotViewport? rawViewport = null)
    {
        if (Bindings.TryGetValue(model, out var old))
        {
            old.Detach(); Bindings.Remove(model);
        }
        var axis = model.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
        if (axis is not null) Bindings.Add(model, new(model, axis, sources, enabled, maximumPoints, rawViewport));
    }

    /// <summary>Immediately retires exact viewport rendering before an asynchronous rebuild.</summary>
    public static void ResumeDownsampling(PlotModel model, int? maximumPoints = null)
    {
        if (!Bindings.TryGetValue(model, out var binding)) return;
        if (maximumPoints.HasValue) binding._maximumPoints = Math.Clamp(maximumPoints.Value, 200, 200_000);
        binding._rawActive = false;
        binding._minimum = binding._maximum = double.NaN;
        foreach (var trace in binding._traces)
        {
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
            var prepared = PlotSeriesRenderer.GetPrepared(source, series, _maximumPoints);
            _traces.Add(new(source, series, prepared));
        }
        // OxyPlot 2.1 exposes these lifecycle events; the pinned version is regression-tested.
#pragma warning disable CS0618
        model.Updating += BeforeUpdate;
        model.Updated += AfterUpdate;
#pragma warning restore CS0618
    }

    private void Detach()
    {
#pragma warning disable CS0618
        _model.Updating -= BeforeUpdate;
        _model.Updated -= AfterUpdate;
#pragma warning restore CS0618
    }

    private void BeforeUpdate(object? sender, EventArgs args)
    {
        foreach (var trace in _traces) trace.Full.Apply(trace.Series);
    }

    private void AfterUpdate(object? sender, EventArgs args)
    {
        var minimum = _axis.ActualMinimum; var maximum = _axis.ActualMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum)) return;
        if (_rawActive && _rawViewport == new PlotViewport(minimum, maximum))
        {
            foreach (var trace in _traces)
                (trace.Raw is not null && trace.RawViewport == _rawViewport ? trace.Raw : trace.Full).Apply(trace.Series);
            return;
        }
        if (_rawActive)
        {
            _rawActive = false;
            foreach (var trace in _traces) ReleaseRaw(trace);
        }

        // Keep density stable within a power-of-two zoom level; further detail returns on zoom.
        var budget = _model.PlotArea.Width > 0
            ? Math.Clamp((int)Math.Ceiling(_model.PlotArea.Width), 200, _maximumPoints) : _maximumPoints;
        if (minimum != _minimum || maximum != _maximum || budget != _budget)
        {
            _minimum = minimum; _maximum = maximum; _budget = budget;
            foreach (var trace in _traces)
            {
                if (trace.Source.Time.Length == 0 || !trace.Source.RangeIndex.SupportsRangeQueries)
                {
                    trace.Visible = trace.Full;
                    continue;
                }
                var fullSpan = trace.Source.Time[^1] - trace.Source.Time[0];
                var fraction = fullSpan > 0 ? Math.Clamp((maximum - minimum) / fullSpan, double.Epsilon, 1) : 1;
                var levelFraction = fraction * Math.Pow(2, Math.Floor(Math.Log2(1 / fraction)));
                var traceBudget = double.IsFinite(levelFraction) ? Math.Max(8, (int)Math.Ceiling(budget * levelFraction)) : budget;
                var (x, y) = trace.Source.RangeIndex.Select(minimum, maximum, traceBudget);
                trace.Visible = PlotSeriesRenderer.CreatePoints(trace.Source, x, y, trace.Series is ScatterSeries);
            }
        }
        foreach (var trace in _traces) (trace.Visible ?? trace.Full).Apply(trace.Series);
    }

    private void ReleaseRaw(Trace trace)
    {
        trace.Raw = null;
        PlotSeriesRenderer.ReleaseRaw(trace.Series);
        // The model is reused during navigation. Release a formerly large exact-view buffer
        // while retaining enough room for the bounded sampled points and gap separators.
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
