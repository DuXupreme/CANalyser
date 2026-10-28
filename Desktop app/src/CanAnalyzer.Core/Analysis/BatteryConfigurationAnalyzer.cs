using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

/// <summary>
/// Reports an explicitly mapped active-pack count or a confirmed manual configuration.
/// Does not infer physical battery presence from voltage, SOC channels or responding nodes.
/// </summary>
public sealed class BatteryConfigurationAnalyzer
{
    public BatteryConfigurationResult Analyze(SignalSeries? activePackCount, int? confirmedManualCount,
        double startSeconds, double endSeconds, double maximumSampleGapSeconds,
        IReadOnlyList<MeasurementGap>? gaps = null, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) || endSeconds <= startSeconds ||
            !double.IsFinite(maximumSampleGapSeconds) || maximumSampleGapSeconds <= 0)
            throw new ArgumentException("Gebruik een geldig tijdvenster en een positieve maximale sampleafstand.");
        if (confirmedManualCount is <= 0)
            throw new ArgumentException("Een bevestigd handmatig accuaantal moet een positief geheel getal zijn.");
        if (activePackCount is not null && confirmedManualCount.HasValue)
            throw new ArgumentException("Kies een accuaantal-signaal of een handmatig bevestigd aantal.");

        var intervals = new List<BatteryCountInterval>();
        var observations = new List<BatteryCountObservation>();
        var source = activePackCount is not null ? BatteryCountSource.ActivePackCountSignal
            : confirmedManualCount.HasValue ? BatteryCountSource.ConfirmedManual : BatteryCountSource.Unknown;
        if (activePackCount is null)
        {
            Add(intervals, startSeconds, endSeconds, confirmedManualCount);
            if (confirmedManualCount is { } manual) observations.Add(new(startSeconds, manual));
        }
        else
        {
            var times = activePackCount.Time;
            var values = activePackCount.Value;
            var mergedGaps = MergeGaps(gaps ?? []);
            var gapIndex = 0;
            var cursor = startSeconds;
            for (var i = 0; i < times.Length; i++)
            {
                if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!double.IsFinite(times[i]) || (i > 0 && (times[i] < times[i - 1] ||
                    (times[i] == times[i - 1] && !values[i].Equals(values[i - 1])))))
                    throw new ArgumentException("Accuaantal-signaal bevat ongeldige tijd of tegenstrijdige waarden op dezelfde tijd.");
            }
            // Include a change exactly at the final sample: remaining-runtime projections must not
            // silently use the old configuration merely because the new one has no duration yet.
            for (var i = 0; i < times.Length && times[i] <= endSeconds; i++)
            {
                if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
                var sampleTime = times[i];
                var isBaseline = sampleTime < startSeconds && i + 1 < times.Length && times[i + 1] > startSeconds &&
                    times[i + 1] - sampleTime <= maximumSampleGapSeconds;
                if ((!isBaseline && sampleTime < startSeconds) || !TryCount(values[i], out var count)) continue;
                while (gapIndex < mergedGaps.Count && mergedGaps[gapIndex].EndSeconds <= sampleTime) gapIndex++;
                var evidenceEnd = isBaseline ? startSeconds : sampleTime;
                if (gapIndex < mergedGaps.Count && mergedGaps[gapIndex].StartSeconds <= evidenceEnd) continue;
                if (observations.Count == 0 || observations[^1].ActivePackCount != count)
                    observations.Add(new(sampleTime, count));
            }
            gapIndex = 0;
            for (var i = 0; i + 1 < times.Length && times[i] < endSeconds; i++)
            {
                if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
                var from = Math.Max(startSeconds, times[i]);
                var to = Math.Min(endSeconds, times[i + 1]);
                if (to <= from || times[i + 1] - times[i] > maximumSampleGapSeconds || !TryCount(values[i], out var count))
                    continue;
                while (gapIndex < mergedGaps.Count && mergedGaps[gapIndex].EndSeconds <= from) gapIndex++;
                // Only the overlapping part of an interval is unknown. Never hold a count through a gap.
                var piece = from;
                for (var g = gapIndex; g < mergedGaps.Count && mergedGaps[g].StartSeconds < to; g++)
                {
                    var untilGap = Math.Min(to, mergedGaps[g].StartSeconds);
                    if (untilGap > piece)
                    {
                        Add(intervals, cursor, piece, null);
                        Add(intervals, piece, untilGap, count);
                        cursor = untilGap;
                    }
                    // After an import gap, require a new count observation; this pair cannot establish it.
                    piece = to;
                }
                if (piece < to)
                {
                    Add(intervals, cursor, piece, null);
                    Add(intervals, piece, to, count);
                    cursor = to;
                }
            }
            Add(intervals, cursor, endSeconds, null);
        }
        return new BatteryConfigurationResult
        {
            Source = source, SignalLabel = activePackCount?.Label, ConfirmedManualCount = confirmedManualCount,
            StartSeconds = startSeconds, EndSeconds = endSeconds, Intervals = intervals, ObservedConfigurations = observations
        };
    }

    private static bool TryCount(double value, out int count)
    {
        count = 0;
        if (!double.IsFinite(value) || value < 0 || value > int.MaxValue || value != Math.Truncate(value)) return false;
        count = (int)value;
        return true;
    }

    private static List<MeasurementGap> MergeGaps(IReadOnlyList<MeasurementGap> gaps)
    {
        var result = new List<MeasurementGap>();
        foreach (var gap in gaps.Where(x => double.IsFinite(x.StartSeconds) && double.IsFinite(x.EndSeconds) && x.EndSeconds > x.StartSeconds)
                     .OrderBy(x => x.StartSeconds))
            if (result.Count > 0 && gap.StartSeconds <= result[^1].EndSeconds)
                result[^1] = result[^1] with { EndSeconds = Math.Max(result[^1].EndSeconds, gap.EndSeconds) };
            else result.Add(gap);
        return result;
    }

    private static void Add(List<BatteryCountInterval> intervals, double start, double end, int? count)
    {
        if (end <= start) return;
        if (intervals.Count > 0 && intervals[^1].EndSeconds == start && intervals[^1].ActivePackCount == count)
            intervals[^1] = intervals[^1] with { EndSeconds = end };
        else intervals.Add(new BatteryCountInterval(start, end, count));
    }
}
