using System.Runtime.CompilerServices;
using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Series;

namespace CanAnalyzer.App.Services;

/// <summary>Prepares plot points on a worker while retaining the complete immutable source.</summary>
public static class PlotSeriesRenderer
{
    private const string PreciseTrackerFormat = "{0}\nTijd: {2:G17}\nWaarde: {4:G17}";
    private static readonly ConditionalWeakTable<Series, PreparedSeries> Prepared = new();
    private static readonly ConditionalWeakTable<IReadOnlyList<MeasurementGap>, MeasurementGapIndex> GapIndexes = new();

    // Imported gaps are immutable and shared by all traces in a dataset. A segment crosses
    // a gap when its lower X is <= the gap start and its upper X is >= the gap end. Sorting
    // starts and retaining suffix-minimum ends answers that query without scanning every
    // gap per point, including for unordered timestamps and overlapping/unsorted gaps.
    private sealed class MeasurementGapIndex
    {
        private readonly double[] _starts;
        private readonly double[] _minimumEnds;

        public MeasurementGapIndex(IReadOnlyList<MeasurementGap> gaps)
        {
            var ordered = gaps.Where(static gap => !double.IsNaN(gap.StartSeconds) && !double.IsNaN(gap.EndSeconds))
                .OrderBy(static gap => gap.StartSeconds).ToArray();
            _starts = new double[ordered.Length];
            _minimumEnds = new double[ordered.Length];
            var minimumEnd = double.PositiveInfinity;
            for (var i = ordered.Length - 1; i >= 0; i--)
            {
                _starts[i] = ordered[i].StartSeconds;
                minimumEnd = Math.Min(minimumEnd, ordered[i].EndSeconds);
                _minimumEnds[i] = minimumEnd;
            }
        }

        public bool CrossesGap(double first, double second)
        {
            if (double.IsNaN(first) || double.IsNaN(second)) return false;
            var minimum = Math.Min(first, second);
            var maximum = Math.Max(first, second);
            var low = 0;
            var high = _starts.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_starts[middle] < minimum) low = middle + 1;
                else high = middle;
            }
            return low < _minimumEnds.Length && _minimumEnds[low] <= maximum;
        }
    }

    internal sealed record PointBuffer(DataPoint[]? Line, ScatterPoint[]? Scatter)
    {
        public void Apply(Series series)
        {
            if (series is LineSeries line)
            {
                line.Points.Clear();
                line.Points.AddRange(Line!);
            }
            else if (series is ScatterSeries scatter)
            {
                scatter.Points.Clear();
                scatter.Points.AddRange(Scatter!);
            }
        }

        public int Count => Line?.Length ?? Scatter!.Length;
    }

    internal sealed record PreparedSeries(PointBuffer Overview, PointBuffer? Raw, PlotViewport? RawViewport);

    /// <summary>
    /// Creates an independent series. Exact mode includes only the captured viewport and edge
    /// neighbors; without a valid viewport a bounded overview is used until one is available.
    /// Call this before publishing the series to a PlotModel attached to a view.
    /// </summary>
    public static Series Build(RenderedSeriesData source, PlotViewOptions options, PlotViewport? viewport = null)
    {
        var maximumPoints = Math.Clamp(options.MaxPointsPerTrace, 200, 200_000);
        var overview = CreateOverview(source, maximumPoints, options.MarkersOnly);
        var exactViewport = !options.UseDownsampling && viewport is { IsValid: true } ? viewport : null;
        PointBuffer? raw = null;
        if (exactViewport is { } interval)
        {
            var (x, y) = SelectExact(source, interval);
            raw = CreatePoints(source, x, y, options.MarkersOnly);
        }

        Series series;
        if (options.MarkersOnly)
        {
            series = new ScatterSeries
            {
                Title = source.Label, MarkerFill = source.Color, MarkerStroke = source.Color,
                MarkerType = MarkerType.Circle, MarkerSize = 2.5, YAxisKey = source.YAxisKey,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed, TrackerFormatString = PreciseTrackerFormat
            };
        }
        else
        {
            LineSeries line = options.StepPlot ? new StairStepSeries() : new LineSeries();
            line.Title = source.Label;
            line.Color = source.Color;
            line.StrokeThickness = 1.2;
            line.YAxisKey = source.YAxisKey;
            line.EdgeRenderingMode = EdgeRenderingMode.PreferSpeed;
            line.Decimator = raw is null ? Decimator.Decimate : null;
            line.TrackerFormatString = PreciseTrackerFormat;
            series = line;
        }

        // Reserve both representations on the worker. Axis updates can then swap the bounded
        // overview and already prepared exact points without allocating a larger UI-side list.
        var capacity = Math.Max(overview.Count, raw?.Count ?? 0);
        if (series is LineSeries preparedLine) preparedLine.Points.EnsureCapacity(capacity);
        else ((ScatterSeries)series).Points.EnsureCapacity(capacity);
        (raw ?? overview).Apply(series);
        Prepared.Add(series, new(overview, raw, exactViewport));
        return series;
    }

    internal static PreparedSeries GetPrepared(RenderedSeriesData source, Series series, int maximumPoints)
    {
        if (Prepared.TryGetValue(series, out var prepared)) return prepared;
        // Compatibility for callers constructing ordinary OxyPlot series themselves. Production
        // builders use Build, which has already prepared this data before UI publication.
        return new(CreateOverview(source, maximumPoints, series is ScatterSeries), null, null);
    }

    internal static void ReleaseRaw(Series series)
    {
        if (!Prepared.TryGetValue(series, out var prepared) || prepared.Raw is null) return;
        Prepared.Remove(series);
        Prepared.Add(series, prepared with { Raw = null, RawViewport = null });
    }

    internal static PointBuffer CreatePoints(RenderedSeriesData source, double[] x, double[] y, bool markers)
    {
        if (markers)
        {
            var points = new List<ScatterPoint>(x.Length);
            for (var i = 0; i < x.Length; i++)
                if (double.IsFinite(x[i]) && double.IsFinite(y[i])) points.Add(new(x[i], y[i]));
            return new(null, points.ToArray());
        }
        if (source.Gaps.Count == 0)
        {
            var points = new DataPoint[x.Length];
            for (var i = 0; i < x.Length; i++) points[i] = new(x[i], y[i]);
            return new(points, null);
        }
        var gapIndex = GapIndexes.GetValue(source.Gaps, static gaps => new MeasurementGapIndex(gaps));
        var line = new List<DataPoint>(x.Length);
        for (var i = 0; i < x.Length; i++)
        {
            if (i > 0 && gapIndex.CrossesGap(x[i - 1], x[i]))
                line.Add(DataPoint.Undefined);
            line.Add(new(x[i], y[i]));
        }
        return new(line.ToArray(), null);
    }

    private static PointBuffer CreateOverview(RenderedSeriesData source, int maximumPoints, bool markers)
    {
        // Access on the worker also initializes the lazy range index before viewport updates.
        var index = source.RangeIndex;
        var (x, y) = index.SupportsRangeQueries
            ? index.Select(null, null, maximumPoints)
            : SelectUnorderedOverview(source, maximumPoints);
        return CreatePoints(source, x, y, markers);
    }

    private static (double[] X, double[] Y) SelectExact(RenderedSeriesData source, PlotViewport viewport)
    {
        if (source.RangeIndex.SupportsRangeQueries)
            return source.RangeIndex.Select(viewport.Minimum, viewport.Maximum, int.MaxValue);

        // Invalid values or unordered timestamps disable binary searching. Preserve original
        // order, include crossing segment endpoints and keep omitted runs disconnected.
        var x = source.Time; var y = source.Value;
        var xs = new List<double>(); var ys = new List<double>();
        var previous = -1;
        for (var i = 0; i < x.Length; i++)
        {
            if (!double.IsFinite(x[i])) continue;
            var inside = x[i] >= viewport.Minimum && x[i] <= viewport.Maximum;
            if (!inside && !(i > 0 && CrossesView(x[i - 1], x[i])) && !(i + 1 < x.Length && CrossesView(x[i], x[i + 1]))) continue;
            if (previous >= 0 && i > previous + 1) { xs.Add(double.NaN); ys.Add(double.NaN); }
            xs.Add(x[i]); ys.Add(y[i]); previous = i;
        }
        return (xs.ToArray(), ys.ToArray());

        bool CrossesView(double a, double b) => double.IsFinite(a) && double.IsFinite(b) &&
            Math.Min(a, b) <= viewport.Maximum && Math.Max(a, b) >= viewport.Minimum;
    }

    private static (double[] X, double[] Y) SelectUnorderedOverview(RenderedSeriesData source, int maximumPoints)
    {
        var x = source.Time; var y = source.Value;
        if (x.Length <= maximumPoints) return (x, y);
        var selected = new SortedSet<int> { 0, x.Length - 1 };
        var minX = -1; var maxX = -1; var minY = -1; var maxY = -1;
        for (var i = 0; i < x.Length; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i])) continue;
            if (minX < 0 || x[i] < x[minX]) minX = i;
            if (maxX < 0 || x[i] > x[maxX]) maxX = i;
            if (minY < 0 || y[i] < y[minY]) minY = i;
            if (maxY < 0 || y[i] > y[maxY]) maxY = i;
        }
        foreach (var extreme in new[] { minX, maxX, minY, maxY }) if (extreme >= 0) selected.Add(extreme);
        var buckets = (maximumPoints - 6) / 2;
        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var first = (int)((long)bucket * x.Length / buckets);
            var end = (int)((long)(bucket + 1) * x.Length / buckets);
            var minimum = -1; var maximum = -1;
            for (var i = first; i < end; i++)
            {
                if (!double.IsFinite(x[i]) || !double.IsFinite(y[i])) continue;
                if (minimum < 0 || y[i] < y[minimum]) minimum = i;
                if (maximum < 0 || y[i] > y[maximum]) maximum = i;
            }
            if (minimum >= 0) selected.Add(minimum);
            if (maximum >= 0) selected.Add(maximum);
        }
        return (selected.Select(i => x[i]).ToArray(), selected.Select(i => y[i]).ToArray());
    }
}
