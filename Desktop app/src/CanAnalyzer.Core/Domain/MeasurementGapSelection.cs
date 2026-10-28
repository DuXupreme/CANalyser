namespace CanAnalyzer.Core.Domain;

/// <summary>Selects only the observation gaps belonging to the signals being used.</summary>
public static class MeasurementGapSelection
{
    public static IReadOnlyList<MeasurementGap> ForChannel(ImportReport? report, string? channel) =>
        report?.GetGapsForChannel(channel) ?? [];

    public static IReadOnlyList<MeasurementGap> ForSeries(ImportReport? report, params SignalSeries?[] series) =>
        ForChannels(report, series.OfType<SignalSeries>().Select(signal => signal.Identity.Channel));

    /// <summary>A multi-input calculation has no joint coverage when any required channel is absent.</summary>
    public static IReadOnlyList<MeasurementGap> ForChannels(ImportReport? report, IEnumerable<string> channels)
    {
        if (report is null) return [];
        var selected = channels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0) return report.Gaps;
        if (selected.Length == 1) return ForChannel(report, selected[0]);
        return Union(selected.SelectMany(channel => ForChannel(report, channel)));
    }

    public static IReadOnlyList<MeasurementGap> Union(IEnumerable<MeasurementGap> gaps)
    {
        var merged = new List<MeasurementGap>();
        foreach (var gap in gaps
                     .Where(gap => double.IsFinite(gap.StartSeconds) && double.IsFinite(gap.EndSeconds) && gap.EndSeconds > gap.StartSeconds)
                     .OrderBy(gap => gap.StartSeconds).ThenBy(gap => gap.EndSeconds))
        {
            if (merged.Count == 0 || gap.StartSeconds > merged[^1].EndSeconds) merged.Add(gap);
            else if (gap.EndSeconds > merged[^1].EndSeconds)
                merged[^1] = new(merged[^1].StartSeconds, gap.EndSeconds);
        }
        return merged;
    }

    /// <summary>Whether a previous sample may still be held at this cursor. The gaps
    /// must be sorted and merged by Union; a value cannot resume after a gap until
    /// its own signal has a fresh sample at or beyond that gap's end.</summary>
    public static bool CanHoldSample(IReadOnlyList<MeasurementGap> orderedMergedGaps, double sampleSeconds, double cursorSeconds)
    {
        var low = 0; var high = orderedMergedGaps.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (orderedMergedGaps[middle].EndSeconds <= sampleSeconds) low = middle + 1; else high = middle;
        }
        return low >= orderedMergedGaps.Count || orderedMergedGaps[low].StartSeconds > cursorSeconds;
    }
}
