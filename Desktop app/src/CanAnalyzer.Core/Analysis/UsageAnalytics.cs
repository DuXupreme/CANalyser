using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

public sealed record UsageBin(double Start, double End, double Seconds);
public readonly record struct JoystickUsagePoint(double Time, double X, double Y, double Seconds);
public sealed record StrokeUsageResult(
    int SampleCount, double? LogMinimum, double? LogMaximum,
    double? Minimum, double? Maximum, double? P01, double? P99,
    double? UsedPercent, double? RobustPercent, double CoveredSeconds,
    double? LowPercent, double? HighPercent, IReadOnlyList<UsageBin> Distribution);
public sealed record JoystickUsageResult(
    IReadOnlyList<JoystickUsagePoint> Points, double CoveredSeconds, double WindowSeconds,
    double? DeadzonePercent, double? SaturationPercent, IReadOnlyList<UsageBin> RadiusDistribution);

/// <summary>Usage from original samples. Long gaps and non-finite values never count as measured time.</summary>
public static class UsageAnalytics
{
    public static StrokeUsageResult AnalyzeStroke(SignalSeries series, double? start = null, double? end = null,
        double edgePercent = 5, int bins = 40)
    {
        var values = series.Value;
        var times = series.Time;
        var (logMin, logMax) = Bounds(values);
        var selected = Enumerable.Range(0, values.Length)
            .Where(i => double.IsFinite(values[i]) && (!start.HasValue || times[i] >= start) && (!end.HasValue || times[i] <= end))
            .Select(i => values[i]).Order().ToArray();
        var span = logMax - logMin;
        var count = Math.Clamp(bins, 10, 100);
        var seconds = new double[count];
        var gapLimit = GapLimit(times);
        var covered = 0d;
        var low = 0d;
        var high = 0d;
        var edge = Math.Clamp(edgePercent, 0, 49) / 100;
        for (var i = 0; i + 1 < times.Length; i++)
        {
            var dt = times[i + 1] - times[i];
            if (dt <= 0 || dt > gapLimit || !double.IsFinite(values[i]) || !double.IsFinite(values[i + 1])) continue;
            var duration = Math.Min(times[i + 1], end ?? times[^1]) - Math.Max(times[i], start ?? times[0]);
            if (duration <= 0) continue;
            covered += duration;
            if (span is > 0)
            {
                var position = (values[i] - logMin!.Value) / span.Value;
                seconds[Math.Clamp((int)(position * count), 0, count - 1)] += duration;
                if (position <= edge) low += duration;
                if (position >= 1 - edge) high += duration;
            }
        }
        var p01 = selected.Length > 0 ? Quantile(selected, 0.01) : (double?)null;
        var p99 = selected.Length > 0 ? Quantile(selected, 0.99) : (double?)null;
        return new StrokeUsageResult(selected.Length, logMin, logMax,
            selected.Length > 0 ? selected[0] : null, selected.Length > 0 ? selected[^1] : null, p01, p99,
            span is > 0 && selected.Length > 0 ? 100 * (selected[^1] - selected[0]) / span : null,
            span is > 0 ? 100 * (p99 - p01) / span : null, covered,
            span is > 0 && covered > 0 ? low * 100 / covered : null,
            span is > 0 && covered > 0 ? high * 100 / covered : null,
            Enumerable.Range(0, count).Select(i => new UsageBin(i * 100d / count, (i + 1) * 100d / count, seconds[i])).ToArray());
    }

    /// <summary>
    /// Last observed value is held only within short source intervals. Both axes must have coverage.
    /// Normalization uses each axis' complete-log min/max, fixed when selecting a time window.
    /// </summary>
    public static JoystickUsageResult AnalyzeJoystick(SignalSeries x, SignalSeries y,
        double? start = null, double? end = null, double deadzone = 0.1, double saturation = 0.9, int bins = 40)
    {
        var count = Math.Clamp(bins, 10, 100);
        var distribution = new double[count];
        var points = new List<JoystickUsagePoint>();
        if (x.Time.Length < 2 || y.Time.Length < 2) return new(points, 0, 0, null, null, []);
        var windowStart = start ?? Math.Min(x.Time[0], y.Time[0]);
        var windowEnd = end ?? Math.Max(x.Time[^1], y.Time[^1]);
        var windowSeconds = Math.Max(0, windowEnd - windowStart);
        var from = Math.Max(Math.Max(x.Time[0], y.Time[0]), windowStart);
        var to = Math.Min(Math.Min(x.Time[^1], y.Time[^1]), windowEnd);
        if (to <= from) return new(points, 0, windowSeconds, null, null, []);
        var (minX, maxX) = Bounds(x.Value);
        var (minY, maxY) = Bounds(y.Value);
        if (!minX.HasValue || !minY.HasValue) return new(points, 0, windowSeconds, null, null, []);
        var gapX = GapLimit(x.Time);
        var gapY = GapLimit(y.Time);
        var ix = 0;
        var iy = 0;
        var cursor = from;
        var covered = 0d;
        var dead = 0d;
        var saturated = 0d;
        while (cursor < to)
        {
            while (ix + 1 < x.Time.Length && x.Time[ix + 1] <= cursor) ix++;
            while (iy + 1 < y.Time.Length && y.Time[iy + 1] <= cursor) iy++;
            if (ix + 1 >= x.Time.Length || iy + 1 >= y.Time.Length) break;
            var next = Math.Min(to, Math.Min(x.Time[ix + 1], y.Time[iy + 1]));
            var valid = x.Time[ix + 1] - x.Time[ix] <= gapX && y.Time[iy + 1] - y.Time[iy] <= gapY
                && double.IsFinite(x.Value[ix]) && double.IsFinite(x.Value[ix + 1])
                && double.IsFinite(y.Value[iy]) && double.IsFinite(y.Value[iy + 1]);
            if (valid && next > cursor)
            {
                var nx = Normalize(x.Value[ix], minX.Value, maxX!.Value);
                var ny = Normalize(y.Value[iy], minY.Value, maxY!.Value);
                var duration = next - cursor;
                var radius = Math.Sqrt(nx * nx + ny * ny);
                points.Add(new(cursor, nx, ny, duration));
                covered += duration;
                if (radius <= deadzone) dead += duration;
                if (radius >= saturation) saturated += duration;
                distribution[Math.Clamp((int)(radius / Math.Sqrt(2) * count), 0, count - 1)] += duration;
            }
            cursor = next;
        }
        return new(points, covered, windowSeconds, covered > 0 ? dead * 100 / covered : null,
            covered > 0 ? saturated * 100 / covered : null,
            Enumerable.Range(0, count).Select(i => new UsageBin(i * Math.Sqrt(2) / count, (i + 1) * Math.Sqrt(2) / count, distribution[i])).ToArray());
    }

    private static double Normalize(double value, double min, double max) => max > min ? 2 * ((value - min) / (max - min)) - 1 : 0;

    private static (double? Min, double? Max) Bounds(double[] values)
    {
        double? min = null, max = null;
        foreach (var value in values)
        {
            if (!double.IsFinite(value)) continue;
            min = min.HasValue ? Math.Min(min.Value, value) : value;
            max = max.HasValue ? Math.Max(max.Value, value) : value;
        }
        return (min, max);
    }

    internal static double GapLimit(double[] times)
    {
        var intervals = new List<double>();
        for (var i = 1; i < times.Length; i++)
            if (times[i] > times[i - 1]) intervals.Add(times[i] - times[i - 1]);
        intervals.Sort();
        return intervals.Count > 0 ? 5 * Quantile(intervals, 0.5) : 0;
    }

    private static double Quantile(IReadOnlyList<double> sorted, double fraction)
    {
        var position = fraction * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
