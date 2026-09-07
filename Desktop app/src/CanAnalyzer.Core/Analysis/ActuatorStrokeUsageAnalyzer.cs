using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

/// <summary>Calculates how much of a configured actuator stroke is actually used.</summary>
public sealed class ActuatorStrokeUsageAnalyzer
{
    public ActuatorStrokeUsageResult Analyze(
        SignalSeries series,
        double referenceMinimum,
        double referenceMaximum,
        double extremeBandPercent = 5,
        double? timeStartSeconds = null,
        double? timeEndSeconds = null)
    {
        if (!double.IsFinite(referenceMinimum) || !double.IsFinite(referenceMaximum) || referenceMaximum <= referenceMinimum)
            throw new ArgumentException("De maximale slagwaarde moet groter zijn dan de minimale slagwaarde.");
        if (!double.IsFinite(extremeBandPercent) || extremeBandPercent is < 0 or > 50)
            throw new ArgumentOutOfRangeException(nameof(extremeBandPercent), "De uiterste-band moet tussen 0 en 50% liggen.");

        var start = timeStartSeconds ?? double.NegativeInfinity;
        var end = timeEndSeconds ?? double.PositiveInfinity;
        if (end < start) (start, end) = (end, start);

        var samples = new List<(double Time, double Value)>();
        var count = Math.Min(series.Time.Length, series.Value.Length);
        for (var index = 0; index < count; index++)
        {
            var time = series.Time[index];
            var value = series.Value[index];
            if (time >= start && time <= end && double.IsFinite(time) && double.IsFinite(value))
                samples.Add((time, value));
        }

        if (samples.Count == 0)
            return new ActuatorStrokeUsageResult(series.Label, 0, referenceMinimum, referenceMaximum,
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, double.NaN, false, false);

        samples.Sort(static (left, right) => left.Time.CompareTo(right.Time));
        var sortedValues = samples.Select(static sample => sample.Value).OrderBy(static value => value).ToArray();
        var p01 = Percentile(sortedValues, 0.01);
        var median = Percentile(sortedValues, 0.50);
        var p99 = Percentile(sortedValues, 0.99);
        var referenceRange = referenceMaximum - referenceMinimum;
        var band = referenceRange * extremeBandPercent / 100d;
        var lowThreshold = referenceMinimum + band;
        var highThreshold = referenceMaximum - band;

        var totalWeight = 0d;
        var lowWeight = 0d;
        var highWeight = 0d;
        var outsideWeight = 0d;
        var hasPositiveDuration = samples.Zip(samples.Skip(1), static (left, right) => right.Time > left.Time).Any(static value => value);
        for (var index = 0; index < samples.Count; index++)
        {
            var weight = index + 1 < samples.Count ? Math.Max(0, samples[index + 1].Time - samples[index].Time) : 0;
            if (!hasPositiveDuration) weight = 1;
            totalWeight += weight;
            var value = samples[index].Value;
            if (value <= lowThreshold) lowWeight += weight;
            if (value >= highThreshold) highWeight += weight;
            if (value < referenceMinimum || value > referenceMaximum) outsideWeight += weight;
        }

        return new ActuatorStrokeUsageResult(
            series.Label,
            samples.Count,
            referenceMinimum,
            referenceMaximum,
            sortedValues[0],
            p01,
            median,
            p99,
            sortedValues[^1],
            ((p99 - p01) / referenceRange) * 100d,
            lowWeight / totalWeight * 100d,
            highWeight / totalWeight * 100d,
            outsideWeight / totalWeight * 100d,
            sortedValues[0] <= lowThreshold,
            sortedValues[^1] >= highThreshold);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return double.NaN;
        if (sorted.Count == 1) return sorted[0];
        var position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }
}
