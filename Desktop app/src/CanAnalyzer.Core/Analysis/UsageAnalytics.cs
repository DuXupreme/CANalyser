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
    double? DeadzonePercent, double? SaturationPercent, IReadOnlyList<UsageBin> RadiusDistribution)
{
    public bool IsCalibrated { get; init; }
    public IReadOnlyList<JoystickAmplitudeBand> AmplitudeBands { get; init; } = [];
    public IReadOnlyList<JoystickInputEpisode> InputEpisodes { get; init; } = [];
    public IReadOnlyList<JoystickFullHold> FullHolds { get; init; } = [];
    public double OutsideCalibrationSeconds { get; init; }
}

/// <summary>Decoded axis values. Neutral need not be the midpoint of the physical limits.</summary>
public sealed record JoystickAxisCalibration(double Minimum, double Neutral, double Maximum)
{
    internal void Validate()
    {
        if (!double.IsFinite(Minimum) || !double.IsFinite(Neutral) || !double.IsFinite(Maximum)
            || Minimum >= Neutral || Neutral >= Maximum)
            throw new ArgumentException("Joystickkalibratie per as: minimum < neutraal < maximum is vereist.");
    }

    internal double Normalize(double value) => value >= Neutral
        ? (value - Neutral) / (Maximum - Neutral)
        : (value - Neutral) / (Neutral - Minimum);
}

public sealed record JoystickAmplitudeBand(string Name, double Lower, double Upper, double Seconds, double Percent);

/// <summary>An input episode is bounded by observed neutral intervals, not by arbitrary sample counts.</summary>
public sealed record JoystickInputEpisode(double StartSeconds, double EndSeconds, double PeakAmplitude,
    bool HasObservedStart, bool ReturnedToNeutral, bool IsShortCorrection)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
    public bool IsComplete => HasObservedStart && ReturnedToNeutral;
}

/// <summary>A continuously observed near-full axis input lasting at least two seconds; gaps split holds.</summary>
public sealed record JoystickFullHold(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

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
        double? start = null, double? end = null, double deadzone = 0.1, double saturation = 0.9, int bins = 40,
        double? calibratedMinimum = null, double? calibratedMaximum = null,
        double maximumGapSeconds = double.PositiveInfinity, IReadOnlyList<MeasurementGap>? gaps = null,
        JoystickAxisCalibration? xCalibration = null, JoystickAxisCalibration? yCalibration = null,
        CancellationToken cancellationToken = default)
    {
        if ((xCalibration is null) != (yCalibration is null))
            throw new ArgumentException("Kalibreer beide joystickassen; alleen X of alleen Y is onvoldoende.");
        cancellationToken.ThrowIfCancellationRequested();
        x.GetData(cancellationToken); y.GetData(cancellationToken);
        xCalibration?.Validate();
        yCalibration?.Validate();
        if (!double.IsFinite(deadzone) || !double.IsFinite(saturation) || deadzone < 0 || saturation > 1 || deadzone >= saturation)
            throw new ArgumentException("Joystickgrenzen: 0 ≤ neutraalgrens < voluitgrens ≤ 1.");
        if ((start.HasValue && !double.IsFinite(start.Value)) || (end.HasValue && !double.IsFinite(end.Value))
            || (start.HasValue && end.HasValue && end.Value < start.Value))
            throw new ArgumentException("Ongeldig joysticktijdvenster.");
        if (calibratedMinimum.HasValue != calibratedMaximum.HasValue ||
            (calibratedMinimum.HasValue && (!double.IsFinite(calibratedMinimum.Value) ||
                !double.IsFinite(calibratedMaximum!.Value) || calibratedMaximum <= calibratedMinimum)))
            throw new ArgumentException("Joystickkalibratie: maximum moet groter zijn dan minimum.");
        if (xCalibration is null && calibratedMinimum.HasValue)
        {
            var neutral = calibratedMinimum.Value / 2 + calibratedMaximum!.Value / 2;
            xCalibration = yCalibration = new(calibratedMinimum.Value, neutral, calibratedMaximum.Value);
        }
        if (double.IsNaN(maximumGapSeconds) || maximumGapSeconds <= 0)
            throw new ArgumentException("Maximale sampleafstand moet positief zijn.");
        _ = new TimedSignalReader(x, maximumGapSeconds);
        _ = new TimedSignalReader(y, maximumGapSeconds);
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
        if (calibratedMinimum.HasValue)
        {
            minX = minY = calibratedMinimum; maxX = maxY = calibratedMaximum;
        }
        if (!minX.HasValue || !minY.HasValue) return new(points, 0, windowSeconds, null, null, []);
        var gapX = Math.Min(maximumGapSeconds, GapLimit(x.Time));
        var gapY = Math.Min(maximumGapSeconds, GapLimit(y.Time));
        var orderedGaps = MeasurementGapSelection.Union(gaps ?? []).ToArray();
        var gapIndex = 0;
        var ix = 0;
        var iy = 0;
        var cursor = from;
        var covered = 0d;
        var dead = 0d;
        var saturated = 0d;
        while (cursor < to)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (ix + 1 < x.Time.Length && x.Time[ix + 1] <= cursor) ix++;
            while (iy + 1 < y.Time.Length && y.Time[iy + 1] <= cursor) iy++;
            if (ix + 1 >= x.Time.Length || iy + 1 >= y.Time.Length) break;
            var next = Math.Min(to, Math.Min(x.Time[ix + 1], y.Time[iy + 1]));
            while (gapIndex < orderedGaps.Length && orderedGaps[gapIndex].EndSeconds <= cursor) gapIndex++;
            var inGap = gapIndex < orderedGaps.Length && orderedGaps[gapIndex].StartSeconds <= cursor;
            if (gapIndex < orderedGaps.Length)
                next = Math.Min(next, inGap ? orderedGaps[gapIndex].EndSeconds : orderedGaps[gapIndex].StartSeconds);
            var valid = x.Time[ix + 1] - x.Time[ix] <= gapX && y.Time[iy + 1] - y.Time[iy] <= gapY
                && !inGap
                && MeasurementGapSelection.CanHoldSample(orderedGaps, x.Time[ix], cursor)
                && MeasurementGapSelection.CanHoldSample(orderedGaps, y.Time[iy], cursor)
                && double.IsFinite(x.Value[ix]) && double.IsFinite(x.Value[ix + 1])
                && double.IsFinite(y.Value[iy]) && double.IsFinite(y.Value[iy + 1]);
            if (valid && next > cursor)
            {
                var nx = xCalibration?.Normalize(x.Value[ix]) ?? Normalize(x.Value[ix], minX.Value, maxX!.Value);
                var ny = yCalibration?.Normalize(y.Value[iy]) ?? Normalize(y.Value[iy], minY.Value, maxY!.Value);
                if (!double.IsFinite(nx) || !double.IsFinite(ny)) { cursor = next; continue; }
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
        var result = new JoystickUsageResult(points, covered, windowSeconds, covered > 0 ? dead * 100 / covered : null,
            covered > 0 ? saturated * 100 / covered : null,
            Enumerable.Range(0, count).Select(i => new UsageBin(i * Math.Sqrt(2) / count, (i + 1) * Math.Sqrt(2) / count, distribution[i])).ToArray());
        return xCalibration is null ? result : AddCalibratedInterpretation(result, deadzone, saturation);
    }

    private static JoystickUsageResult AddCalibratedInterpretation(JoystickUsageResult result, double deadzone, double saturation)
    {
        // Axis amplitude avoids calling a diagonal 65%/65% input "full" on either axis.
        // The original radial histogram remains available independently for the XY distribution.
        var small = Math.Clamp(0.30, deadzone, saturation);
        var medium = Math.Clamp(0.60, small, saturation);
        var limits = new[] { deadzone, small, medium, saturation, 1.0 };
        var names = new[] { "Neutraal", "Kleine uitslag", "Middelgrote uitslag", "Grote uitslag", "Bijna / volledig voluit" };
        var seconds = new double[5];
        var episodes = new List<JoystickInputEpisode>();
        var holds = new List<JoystickFullHold>();
        double? episodeStart = null, holdStart = null;
        var episodePeak = 0d;
        var observedStart = false;
        var previousNeutral = false;
        var previousEnd = double.NaN;
        var outside = 0d;

        void EndEpisode(double time, bool returned)
        {
            if (episodeStart is not double beginning) return;
            var complete = observedStart && returned;
            episodes.Add(new(beginning, time, episodePeak, observedStart, returned,
                complete && time - beginning <= 1.0 + 1e-9 && episodePeak <= small));
            episodeStart = null;
        }
        void EndHold(double time)
        {
            if (holdStart is double beginning && time - beginning >= 2.0 - 1e-9)
                holds.Add(new(beginning, time));
            holdStart = null;
        }

        foreach (var point in result.Points)
        {
            if (!double.IsNaN(previousEnd) && Math.Abs(point.Time - previousEnd) > 1e-9)
            {
                EndEpisode(previousEnd, false);
                EndHold(previousEnd);
                previousNeutral = false;
            }
            var amplitude = Math.Max(Math.Abs(point.X), Math.Abs(point.Y));
            var neutral = amplitude <= deadzone;
            var full = amplitude >= saturation;
            var band = neutral ? 0 : full ? 4 : amplitude <= small ? 1 : amplitude <= medium ? 2 : 3;
            seconds[band] += point.Seconds;
            if (amplitude > 1) outside += point.Seconds;
            if (neutral) EndEpisode(point.Time, true);
            else
            {
                if (episodeStart is null)
                {
                    episodeStart = point.Time;
                    observedStart = previousNeutral;
                    episodePeak = 0;
                }
                episodePeak = Math.Max(episodePeak, amplitude);
            }
            if (full) holdStart ??= point.Time;
            else EndHold(point.Time);
            previousNeutral = neutral;
            previousEnd = point.Time + point.Seconds;
        }
        if (!double.IsNaN(previousEnd))
        {
            EndEpisode(previousEnd, false);
            EndHold(previousEnd);
        }
        return result with
        {
            IsCalibrated = true,
            AmplitudeBands = Enumerable.Range(0, 5).Select(i => new JoystickAmplitudeBand(names[i],
                i == 0 ? 0 : limits[i - 1], limits[i], seconds[i],
                result.CoveredSeconds > 0 ? 100 * seconds[i] / result.CoveredSeconds : 0)).ToArray(),
            InputEpisodes = episodes,
            FullHolds = holds,
            OutsideCalibrationSeconds = outside
        };
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
