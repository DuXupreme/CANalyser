using System.Runtime.CompilerServices;
using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Utilities;
using OxyPlot;
using OxyPlot.Series;

namespace CanAnalyzer.App.Services;

/// <summary>Prepares plot points on a worker while retaining the complete immutable source.</summary>
public static class PlotSeriesRenderer
{
    private const string PreciseTrackerFormat = "{0}\nTijd: {2:G17}\nWaarde: {4:G17}";
    private static readonly ConditionalWeakTable<Series, PreparedSeries> Prepared = new();
    private sealed class AppliedBuffer { public PointBuffer? Value; }
    private static readonly ConditionalWeakTable<Series, AppliedBuffer> Applied = new();
    private static readonly ConditionalWeakTable<IReadOnlyList<MeasurementGap>, MeasurementGapIndex> GapIndexes = new();

    // Imported gaps are immutable and shared by all traces in a dataset. A segment crosses
    // a gap when its lower X is <= the gap start and its upper X is >= the gap end. Sorting
    // starts and retaining suffix-minimum ends answers that query without scanning every
    // gap per point, including for unordered timestamps and overlapping/unsorted gaps.
    private sealed class MeasurementGapIndex
    {
        private readonly double[] _starts;
        private readonly double[] _minimumEnds;

        public MeasurementGapIndex(IReadOnlyList<MeasurementGap> gaps, CancellationToken cancellationToken)
        {
            var valid = new List<(double Start, double End)>();
            var visited = 0;
            foreach (var gap in gaps)
            {
                if ((visited++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!double.IsNaN(gap.StartSeconds) && !double.IsNaN(gap.EndSeconds))
                    valid.Add((gap.StartSeconds, gap.EndSeconds));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = CancellableArrayOperations.ToArray(valid, cancellationToken);
            CancellableArrayOperations.Sort(ordered, static (a, b) => a.Start.CompareTo(b.Start), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _starts = new double[ordered.Length];
            _minimumEnds = new double[ordered.Length];
            var minimumEnd = double.PositiveInfinity;
            for (var i = ordered.Length - 1; i >= 0; i--)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                _starts[i] = ordered[i].Start;
                minimumEnd = Math.Min(minimumEnd, ordered[i].End);
                _minimumEnds[i] = minimumEnd;
            }
            cancellationToken.ThrowIfCancellationRequested();
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
        public void Apply(Series series, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applied = Applied.GetOrCreateValue(series);
            if (ReferenceEquals(applied.Value, this)) return;
            if (series is LineSeries line)
            {
                line.Points.Clear();
                CancellableArrayOperations.AddRange(line.Points, Line!, cancellationToken);
            }
            else if (series is ScatterSeries scatter)
            {
                scatter.Points.Clear();
                CancellableArrayOperations.AddRange(scatter.Points, Scatter!, cancellationToken);
            }
            if (series is IOverviewSeries bounded) bounded.ResetWindow();
            applied.Value = this;
        }

        public int Count => Line?.Length ?? Scatter!.Length;
    }

    internal sealed record PreparedSeries(PointBuffer Overview, PointBuffer? Raw, PlotViewport? RawViewport);

    /// <summary>
    /// Creates an independent series. Exact mode includes only the captured viewport and edge
    /// neighbors; without a valid viewport a bounded overview is used until one is available.
    /// Call this before publishing the series to a PlotModel attached to a view.
    /// </summary>
    public static Series Build(RenderedSeriesData source, PlotViewOptions options, PlotViewport? viewport = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maximumPoints = Math.Clamp(options.MaxPointsPerTrace, 200, 200_000);
        var overview = CreateOverview(source, maximumPoints, options.MarkersOnly, cancellationToken);
        var exactViewport = !options.UseDownsampling && viewport is { IsValid: true } ? viewport : null;
        PointBuffer? raw = null;
        if (exactViewport is { } interval)
        {
            var (x, y) = SelectExact(source, interval, cancellationToken);
            raw = CreatePoints(source, x, y, options.MarkersOnly, cancellationToken);
        }

        Series series;
        if (options.MarkersOnly)
        {
            series = new OverviewScatterSeries(CancellableArrayOperations.ToList(overview.Scatter!, cancellationToken))
            {
                Title = source.Label, MarkerFill = source.Color, MarkerStroke = source.Color,
                MarkerType = MarkerType.Circle, MarkerSize = 2.5, YAxisKey = source.YAxisKey,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed, TrackerFormatString = PreciseTrackerFormat
            };
        }
        else
        {
            var bounds = CancellableArrayOperations.ToList(overview.Line!, cancellationToken);
            LineSeries line = options.StepPlot ? new OverviewStepSeries(bounds) : new OverviewLineSeries(bounds);
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
        (raw ?? overview).Apply(series, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Prepared.Add(series, new(overview, raw, exactViewport));
        return series;
    }

    internal static PreparedSeries GetPrepared(RenderedSeriesData source, Series series, int maximumPoints,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Prepared.TryGetValue(series, out var prepared)) return prepared;
        // Compatibility for callers constructing ordinary OxyPlot series themselves. Production
        // builders use Build, which has already prepared this data before UI publication.
        return new(CreateOverview(source, maximumPoints, series is ScatterSeries, cancellationToken), null, null);
    }

    internal static void ReleaseRaw(Series series)
    {
        if (!Prepared.TryGetValue(series, out var prepared) || prepared.Raw is null) return;
        Prepared.Remove(series);
        Prepared.Add(series, prepared with { Raw = null, RawViewport = null });
    }

    internal static PointBuffer CreatePoints(RenderedSeriesData source, double[] x, double[] y, bool markers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (markers)
        {
            var points = new List<ScatterPoint>(x.Length);
            for (var i = 0; i < x.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (double.IsFinite(x[i]) && double.IsFinite(y[i])) points.Add(new(x[i], y[i]));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(null, CancellableArrayOperations.ToArray(points, cancellationToken));
        }
        if (source.Gaps.Count == 0)
        {
            var points = new DataPoint[x.Length];
            for (var i = 0; i < x.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                points[i] = new(x[i], y[i]);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(points, null);
        }
        var gapIndex = GapIndexes.GetValue(source.Gaps, gaps => new MeasurementGapIndex(gaps, cancellationToken));
        var line = new List<DataPoint>(x.Length);
        for (var i = 0; i < x.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (i > 0 && gapIndex.CrossesGap(x[i - 1], x[i]))
                line.Add(DataPoint.Undefined);
            line.Add(new(x[i], y[i]));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(CancellableArrayOperations.ToArray(line, cancellationToken), null);
    }

    internal static PointBuffer CreateOverview(RenderedSeriesData source, int maximumPoints, bool markers,
        CancellationToken cancellationToken = default)
    {
        // Access on the worker also initializes the lazy range index before viewport updates.
        var index = source.GetRangeIndex(cancellationToken);
        var (x, y) = index.SupportsRangeQueries
            ? index.Select(null, null, maximumPoints, cancellationToken)
            : SelectUnorderedOverview(source, maximumPoints, cancellationToken);
        return CreatePoints(source, x, y, markers, cancellationToken);
    }

    private static (double[] X, double[] Y) SelectExact(RenderedSeriesData source, PlotViewport viewport,
        CancellationToken cancellationToken)
    {
        var index = source.GetRangeIndex(cancellationToken);
        if (index.SupportsRangeQueries)
            return index.Select(viewport.Minimum, viewport.Maximum, int.MaxValue, cancellationToken);

        // Invalid values or unordered timestamps disable binary searching. Preserve original
        // order, include crossing segment endpoints and keep omitted runs disconnected.
        var x = source.Time; var y = source.Value;
        var xs = new List<double>(); var ys = new List<double>();
        var previous = -1;
        for (var i = 0; i < x.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(x[i])) continue;
            var inside = x[i] >= viewport.Minimum && x[i] <= viewport.Maximum;
            if (!inside && !(i > 0 && CrossesView(x[i - 1], x[i])) && !(i + 1 < x.Length && CrossesView(x[i], x[i + 1]))) continue;
            if (previous >= 0 && i > previous + 1) { xs.Add(double.NaN); ys.Add(double.NaN); }
            xs.Add(x[i]); ys.Add(y[i]); previous = i;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (CancellableArrayOperations.ToArray(xs, cancellationToken), CancellableArrayOperations.ToArray(ys, cancellationToken));

        bool CrossesView(double a, double b) => double.IsFinite(a) && double.IsFinite(b) &&
            Math.Min(a, b) <= viewport.Maximum && Math.Max(a, b) >= viewport.Minimum;
    }

    private static (double[] X, double[] Y) SelectUnorderedOverview(RenderedSeriesData source, int maximumPoints,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var x = source.Time; var y = source.Value;
        if (x.Length <= maximumPoints) return (x, y);
        var selected = new SortedSet<int> { 0, x.Length - 1 };
        var minX = -1; var maxX = -1; var minY = -1; var maxY = -1;
        for (var i = 0; i < x.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
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
            if ((bucket & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var first = (int)((long)bucket * x.Length / buckets);
            var end = (int)((long)(bucket + 1) * x.Length / buckets);
            var minimum = -1; var maximum = -1;
            for (var i = first; i < end; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!double.IsFinite(x[i]) || !double.IsFinite(y[i])) continue;
                if (minimum < 0 || y[i] < y[minimum]) minimum = i;
                if (maximum < 0 || y[i] > y[maximum]) maximum = i;
            }
            if (minimum >= 0) selected.Add(minimum);
            if (maximum >= 0) selected.Add(maximum);
        }
        var xs = new double[selected.Count]; var ys = new double[selected.Count];
        var output = 0;
        foreach (var i in selected)
        {
            if ((output & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            xs[output] = x[i]; ys[output++] = y[i];
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (xs, ys);
    }
}
