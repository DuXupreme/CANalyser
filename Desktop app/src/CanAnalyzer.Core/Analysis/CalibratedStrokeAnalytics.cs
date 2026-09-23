using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

/// <summary>All positions and half-ranges use the selected feedback signal's decoded unit.</summary>
public sealed record StrokeReference(double? Center = null, double HalfRange = double.NaN,
    SignalSeries? CenterSignal = null, SignalSeries? HalfRangeSignal = null,
    SignalSeries? Setpoint = null, SignalSeries? RunSignal = null, double RunValue = 1,
    double MaximumGapSeconds = 0.5);

public sealed record CalibratedStrokeResult(double WindowSeconds, double CoveredSeconds,
    double? MinimumPercent, double? MaximumPercent, double? P01Percent, double? P99Percent,
    double? LowPercent, double? HighPercent, double? OutsidePercent,
    int LowVisits, int HighVisits, double LongestLowSeconds, double LongestHighSeconds,
    double SetpointCoveredSeconds, double? SetpointLowPercent, double? SetpointHighPercent,
    double? BothLowPercent, double? BothHighPercent, double? MeanAbsoluteErrorPercent,
    IReadOnlyList<UsageBin> Distribution);

/// <summary>Generic calibrated range analysis; no machine-specific control law is assumed.</summary>
public static class CalibratedStrokeAnalytics
{
    public static CalibratedStrokeResult Analyze(SignalSeries position, StrokeReference reference,
        double? start = null, double? end = null, double edgePercent = 5, int bins = 40,
        IReadOnlyList<MeasurementGap>? gaps = null)
    {
        if (reference.CenterSignal is null && (!reference.Center.HasValue || !double.IsFinite(reference.Center.Value)))
            throw new ArgumentException("Kies een nulpunt-signaal of vul een bekend nulpunt in.");
        if ((reference.HalfRangeSignal is null && (!double.IsFinite(reference.HalfRange) || reference.HalfRange <= 0)) ||
            !double.IsFinite(reference.MaximumGapSeconds) || reference.MaximumGapSeconds <= 0 ||
            !double.IsFinite(reference.RunValue) || !double.IsFinite(edgePercent) || edgePercent is <= 0 or >= 50)
            throw new ArgumentException("Halfbereik en maximale sampleafstand moeten positief zijn; grensband tussen 0 en 50%.");
        var sources = new[] { position, reference.CenterSignal, reference.HalfRangeSignal, reference.RunSignal, reference.Setpoint };
        var readers = sources.Select(s => s is null ? null : new TimedSignalReader(s, reference.MaximumGapSeconds)).ToArray();
        var from = start ?? (position.Time.Length > 0 ? position.Time[0] : 0);
        var to = end ?? (position.Time.Length > 0 ? position.Time[^1] : from);
        if (!double.IsFinite(from) || !double.IsFinite(to) || to < from)
            throw new ArgumentException("Ongeldig tijdvenster.");
        var histogram = new double[Math.Clamp(bins, 10, 100)];
        var weighted = new List<(double Value, double Seconds)>();
        var covered = 0d; var low = 0d; var high = 0d; var outside = 0d;
        var commandCovered = 0d; var commandLow = 0d; var commandHigh = 0d;
        var bothLow = 0d; var bothHigh = 0d; var error = 0d;
        var lowVisits = 0; var highVisits = 0;
        var lowRun = 0d; var highRun = 0d; var longestLow = 0d; var longestHigh = 0d;
        var orderedGaps = (gaps ?? []).OrderBy(g => g.StartSeconds).ToArray();
        var gapIndex = 0;
        var cursor = from;
        while (cursor < to)
        {
            var next = to;
            foreach (var reader in readers)
                if (reader is not null) next = Math.Min(next, reader.NextBoundary(cursor, to));
            while (gapIndex < orderedGaps.Length && orderedGaps[gapIndex].EndSeconds <= cursor) gapIndex++;
            var inGap = gapIndex < orderedGaps.Length && orderedGaps[gapIndex].StartSeconds <= cursor;
            if (gapIndex < orderedGaps.Length)
                next = Math.Min(next, inGap ? orderedGaps[gapIndex].EndSeconds : orderedGaps[gapIndex].StartSeconds);
            if (next <= cursor) break;
            var measured = 0d;
            var valid = !inGap && readers[0]!.TryValue(cursor, out measured);
            var center = reference.Center ?? 0;
            var half = reference.HalfRange;
            if (readers[1] is { } cr)
                valid &= cr.TryValue(cursor, out center) && cr.IsConstantInterval(cursor);
            if (readers[2] is { } hr) valid &= hr.TryValue(cursor, out half);
            if (readers[3] is { } run) valid &= run.TryValue(cursor, out var state) && state == reference.RunValue;
            if (valid && half > 0 && double.IsFinite(half))
            {
                var value = 50 + 50 * (measured - center) / half;
                if (!double.IsFinite(value)) { lowRun = highRun = 0; cursor = next; continue; }
                var duration = next - cursor;
                covered += duration;
                weighted.Add((value, duration));
                var isOutside = value < 0 || value > 100;
                var isLow = !isOutside && value <= edgePercent;
                var isHigh = !isOutside && value >= 100 - edgePercent;
                if (isOutside) outside += duration;
                else histogram[Math.Clamp((int)(value / 100 * histogram.Length), 0, histogram.Length - 1)] += duration;
                if (isLow) { low += duration; if (lowRun == 0) lowVisits++; lowRun += duration; }
                else lowRun = 0;
                if (isHigh) { high += duration; if (highRun == 0) highVisits++; highRun += duration; }
                else highRun = 0;
                longestLow = Math.Max(longestLow, lowRun); longestHigh = Math.Max(longestHigh, highRun);
                if (readers[4] is { } cmd && cmd.TryValue(cursor, out var target))
                {
                    var targetPercent = 50 + 50 * (target - center) / half;
                    if (double.IsFinite(targetPercent))
                    {
                        commandCovered += duration;
                        var asksLow = targetPercent <= edgePercent;
                        var asksHigh = targetPercent >= 100 - edgePercent;
                        if (asksLow) commandLow += duration;
                        if (asksHigh) commandHigh += duration;
                        if (asksLow && isLow) bothLow += duration;
                        if (asksHigh && isHigh) bothHigh += duration;
                        error += Math.Abs(value - targetPercent) * duration;
                    }
                }
            }
            else lowRun = highRun = 0;
            cursor = next;
        }
        weighted.Sort((a, b) => a.Value.CompareTo(b.Value));
        double? Percentage(double seconds, double total) => total > 0 ? seconds * 100 / total : null;
        double? Quantile(double q)
        {
            if (covered <= 0) return null;
            var sum = 0d;
            foreach (var item in weighted) { sum += item.Seconds; if (sum >= q * covered) return item.Value; }
            return weighted[^1].Value;
        }
        return new(to - from, covered, weighted.Count > 0 ? weighted[0].Value : null,
            weighted.Count > 0 ? weighted[^1].Value : null, Quantile(.01), Quantile(.99),
            Percentage(low, covered), Percentage(high, covered), Percentage(outside, covered),
            lowVisits, highVisits, longestLow, longestHigh, commandCovered,
            Percentage(commandLow, commandCovered), Percentage(commandHigh, commandCovered),
            Percentage(bothLow, commandCovered), Percentage(bothHigh, commandCovered),
            commandCovered > 0 ? error / commandCovered : null,
            histogram.Select((v, i) => new UsageBin(i * 100d / histogram.Length, (i + 1) * 100d / histogram.Length, v)).ToArray());
    }
}

/// <summary>Forward-only sample-and-hold reader. Never extrapolates or bridges stale/invalid data.</summary>
internal sealed class TimedSignalReader
{
    private readonly SignalSeries _series;
    private readonly double _gap;
    private int _index;
    public TimedSignalReader(SignalSeries series, double maxGap)
    {
        _series = series;
        for (var i = 0; i < series.Time.Length; i++)
            if (!double.IsFinite(series.Time[i]) || (i > 0 && series.Time[i] < series.Time[i - 1]))
                throw new ArgumentException($"Signaal {series.Label}: ongeldige of aflopende tijd.");
        var adaptive = UsageAnalytics.GapLimit(series.Time);
        _gap = Math.Min(maxGap, adaptive > 0 ? adaptive : maxGap);
    }
    private void Advance(double time)
    {
        while (_index + 1 < _series.Time.Length && _series.Time[_index + 1] <= time) _index++;
    }
    public double NextBoundary(double time, double end)
    {
        Advance(time);
        if (_series.Time.Length == 0) return end;
        if (_series.Time[_index] > time) return Math.Min(end, _series.Time[_index]);
        return _index + 1 < _series.Time.Length ? Math.Min(end, _series.Time[_index + 1]) : end;
    }
    public bool TryValue(double time, out double value)
    {
        Advance(time); value = 0;
        if (_index + 1 >= _series.Time.Length || _series.Time[_index] > time ||
            _series.Time[_index + 1] - _series.Time[_index] > _gap ||
            !double.IsFinite(_series.Value[_index]) || !double.IsFinite(_series.Value[_index + 1])) return false;
        value = _series.Value[_index]; return true;
    }
    public bool IsConstantInterval(double time)
    {
        Advance(time);
        return _index + 1 < _series.Value.Length && _series.Value[_index] == _series.Value[_index + 1];
    }
}
