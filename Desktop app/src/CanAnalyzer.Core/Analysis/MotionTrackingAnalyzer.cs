using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

public sealed record MotionTrackingResult(double CoveredSeconds, double MovingSeconds,
    double? MeanAbsoluteError, double? Percentile95AbsoluteError, double? MaximumAbsoluteError,
    double? WorstTimeSeconds);

/// <summary>Time-weighted tracking error during detectable changes, using original sample-and-hold values.</summary>
public static class MotionTrackingAnalyzer
{
    public static MotionTrackingResult Analyze(SignalSeries command, SignalSeries feedback,
        double commandTolerance, double feedbackTolerance, double maximumGapSeconds = .5,
        IReadOnlyList<MeasurementGap>? gaps = null, SignalSeries? runSignal = null, double runValue = 1,
        CancellationToken cancellationToken = default, double? startSeconds = null, double? endSeconds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        command.GetData(cancellationToken); feedback.GetData(cancellationToken); runSignal?.GetData(cancellationToken);
        if (startSeconds.HasValue && !double.IsFinite(startSeconds.Value) || endSeconds.HasValue && !double.IsFinite(endSeconds.Value) ||
            startSeconds.HasValue && endSeconds.HasValue && endSeconds <= startSeconds)
            throw new ArgumentException("Het volgvenster moet eindig zijn en een positief bereik hebben.");
        if (!double.IsFinite(commandTolerance) || commandTolerance <= 0 ||
            !double.IsFinite(feedbackTolerance) || feedbackTolerance <= 0 ||
            !double.IsFinite(maximumGapSeconds) || maximumGapSeconds <= 0)
            throw new ArgumentException("Bewegingstoleranties en maximale sampleafstand moeten positief zijn.");
        if (command.Time.Length < 2 || feedback.Time.Length < 2) return new(0, 0, null, null, null, null);
        var from = Math.Max(command.Time[0], feedback.Time[0]);
        var end = Math.Min(endSeconds ?? double.PositiveInfinity, Math.Min(command.Time[^1], feedback.Time[^1]));
        var cr = new TimedSignalReader(command, maximumGapSeconds, gaps);
        var fr = new TimedSignalReader(feedback, maximumGapSeconds, gaps);
        var rr = runSignal is null ? null : new TimedSignalReader(runSignal, maximumGapSeconds, gaps);
        var orderedGaps = MeasurementGapSelection.Union(gaps ?? []).ToArray();
        var gapIndex = 0;
        var weighted = new List<(double Error, double Seconds)>();
        double covered = 0, moving = 0, integral = 0, maximum = 0;
        double? worstTime = null;
        double commandAnchor = 0, feedbackAnchor = 0, movingUntil = double.NegativeInfinity;
        var hasBaseline = false;
        for (var cursor = from; cursor < end;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = Math.Min(cr.NextBoundary(cursor, end), fr.NextBoundary(cursor, end));
            if (rr is not null) next = Math.Min(next, rr.NextBoundary(cursor, end));
            while (gapIndex < orderedGaps.Length && orderedGaps[gapIndex].EndSeconds <= cursor) gapIndex++;
            var inGap = gapIndex < orderedGaps.Length && orderedGaps[gapIndex].StartSeconds <= cursor;
            if (gapIndex < orderedGaps.Length)
                next = Math.Min(next, inGap ? orderedGaps[gapIndex].EndSeconds : orderedGaps[gapIndex].StartSeconds);
            if (next <= cursor) break;
            var valid = cr.TryValue(cursor, out var c) & fr.TryValue(cursor, out var f);
            valid &= !inGap && (rr is null || (rr.TryValue(cursor, out var state) && state == runValue));
            if (!valid)
            {
                hasBaseline = false;
                movingUntil = double.NegativeInfinity;
                cursor = next;
                continue;
            }
            if (!hasBaseline) { commandAnchor = c; feedbackAnchor = f; hasBaseline = true; }
            if (Math.Abs(c - commandAnchor) >= commandTolerance || Math.Abs(f - feedbackAnchor) >= feedbackTolerance)
            {
                movingUntil = cursor + .1;
                commandAnchor = c;
                feedbackAnchor = f;
            }
            var includedStart = Math.Max(cursor, startSeconds ?? double.NegativeInfinity);
            var seconds = Math.Max(0, next - includedStart);
            covered += seconds;
            var activeSeconds = Math.Max(0, Math.Min(next, movingUntil) - includedStart);
            if (activeSeconds > 0)
            {
                var error = Math.Abs(c - f);
                if (double.IsFinite(error))
                {
                    moving += activeSeconds;
                    integral += error * activeSeconds;
                    weighted.Add((error, activeSeconds));
                    if (!worstTime.HasValue || error > maximum) { maximum = error; worstTime = includedStart; }
                }
            }
            cursor = next;
        }
        if (moving <= 0) return new(covered, 0, null, null, null, null);
        weighted.Sort((a, b) => a.Error.CompareTo(b.Error));
        var cumulative = 0d;
        var p95 = weighted[^1].Error;
        foreach (var value in weighted)
        {
            cumulative += value.Seconds;
            if (cumulative >= .95 * moving) { p95 = value.Error; break; }
        }
        return new(covered, moving, integral / moving, p95, maximum, worstTime);
    }
}
