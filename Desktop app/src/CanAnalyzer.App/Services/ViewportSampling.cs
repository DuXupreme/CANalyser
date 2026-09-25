using System.Runtime.CompilerServices;
using CanAnalyzer.App.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace CanAnalyzer.App.Services;

/// <summary>
/// Supplies ordinary OxyPlot series with extrema from the visible source interval. Full-range
/// points participate in OxyPlot's data/axis update; viewport points are supplied afterwards,
/// before rendering. Reset, autoscale and hiding series therefore keep their original meaning.
/// No render override, source mutation, or worker access to an attached model is needed.
/// </summary>
public sealed class ViewportSampling
{
    private static readonly ConditionalWeakTable<PlotModel, ViewportSampling> Bindings = new();
    private readonly PlotModel _model;
    private readonly Axis _axis;
    private readonly List<Trace> _traces = [];
    private readonly int _maximumPoints;
    private double _minimum = double.NaN, _maximum = double.NaN;
    private int _budget;

    private sealed class Trace(RenderedSeriesData source, OxyPlot.Series.Series series)
    {
        public RenderedSeriesData Source { get; } = source;
        public OxyPlot.Series.Series Series { get; } = series;
        public DataPoint[] Full { get; } = series is LineSeries line ? line.Points.ToArray()
            : ((ScatterSeries)series).Points.Select(p => new DataPoint(p.X, p.Y)).ToArray();
        public DataPoint[]? Visible { get; set; }
        public ScatterPoint[]? FullScatter { get; } = (series as ScatterSeries)?.Points.ToArray();
        public ScatterPoint[]? VisibleScatter { get; set; }

        public void Apply(bool full)
        {
            if (Visible is null) return;
            if (Series is LineSeries line)
            { line.Points.Clear(); line.Points.AddRange(full ? Full : Visible); }
            else if (Series is ScatterSeries scatter)
            { scatter.Points.Clear(); scatter.Points.AddRange(full ? FullScatter! : VisibleScatter!); }
        }
    }

    public static void Attach(PlotModel model, IReadOnlyList<RenderedSeriesData> sources, bool enabled, int maximumPoints)
    {
        if (Bindings.TryGetValue(model, out var old))
        {
            old.Detach(); Bindings.Remove(model);
        }
        if (!enabled) return;
        var axis = model.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
        if (axis is not null) Bindings.Add(model, new(model, axis, sources, maximumPoints));
    }

    private ViewportSampling(PlotModel model, Axis axis, IReadOnlyList<RenderedSeriesData> sources, int maximumPoints)
    {
        _model = model; _axis = axis; _maximumPoints = Math.Clamp(maximumPoints, 200, 200_000);
        foreach (var (source, series) in sources.Zip(model.Series))
        {
            if (source.Time.Length <= 200 || series is not (LineSeries or ScatterSeries)) continue;
            if (source.RangeIndex.SupportsRangeQueries) _traces.Add(new(source, series));
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
        foreach (var trace in _traces) trace.Apply(full: true);
    }

    private void AfterUpdate(object? sender, EventArgs args)
    {
        var minimum = _axis.ActualMinimum; var maximum = _axis.ActualMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum)) return;
        // A min/max pair per two horizontal pixels; further detail returns on zoom.
        // Keep density stable within a power-of-two zoom level, rather than increasing
        // the number of drawn points immediately on every small pan/zoom gesture.
        var budget = _model.PlotArea.Width > 0
            ? Math.Clamp((int)Math.Ceiling(_model.PlotArea.Width), 200, _maximumPoints) : _maximumPoints;
        if (minimum != _minimum || maximum != _maximum || budget != _budget)
        {
            _minimum = minimum; _maximum = maximum; _budget = budget;
            foreach (var trace in _traces)
            {
                var fullSpan = trace.Source.Time[^1] - trace.Source.Time[0];
                var fraction = fullSpan > 0 ? Math.Clamp((maximum - minimum) / fullSpan, double.Epsilon, 1) : 1;
                var levelFraction = fraction * Math.Pow(2, Math.Floor(Math.Log2(1 / fraction)));
                var traceBudget = double.IsFinite(levelFraction) ? Math.Max(8, (int)Math.Ceiling(budget * levelFraction)) : budget;
                var (x, y) = trace.Source.RangeIndex.Select(minimum, maximum, traceBudget);
                var points = new List<DataPoint>(x.Length);
                for (var i = 0; i < x.Length; i++)
                {
                    if (trace.Series is LineSeries && i > 0 && trace.Source.Gaps.Any(g => x[i - 1] <= g.StartSeconds && x[i] >= g.EndSeconds))
                        points.Add(DataPoint.Undefined);
                    points.Add(new(x[i], y[i]));
                }
                trace.Visible = points.ToArray();
                if (trace.Series is ScatterSeries) trace.VisibleScatter = points.Select(p => new ScatterPoint(p.X, p.Y)).ToArray();
            }
        }
        foreach (var trace in _traces) trace.Apply(full: false);
    }
}
