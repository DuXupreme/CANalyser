using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

/// <summary>
/// Time-weighted usage from original signal samples. Never bridges an import gap or a stale
/// signal. SOC is interpolated only between valid adjacent observations for energy allocation.
/// </summary>
public sealed class ActiveUsageAnalyzer
{
    private readonly record struct Span(double Start, double End, bool Value);
    private readonly record struct SocSpan(double Start, double End, double First, double Last)
    {
        public double At(double time) => First + (Last - First) * (time - Start) / (End - Start);
    }

    public ActiveUsageResult Analyze(SignalSeries? activity, SignalSeries? soc, SignalSeries? on,
        ActiveUsageOptions options, IReadOnlyList<MeasurementGap>? gaps = null, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var source = options.Method == ActivityDetectionMethod.SocSlope ? soc : activity;
        if (source is null) throw new ArgumentException(options.Method == ActivityDetectionMethod.SocSlope
            ? "Kies een SOC-signaal voor detectie op SOC-daling." : "Kies een activiteitssignaal, bijvoorbeeld motor-RPM.");
        var inputs = new[] { source, soc, on }.OfType<SignalSeries>().Distinct().ToArray();
        foreach (var input in inputs) ValidateSeries(input, cancellationToken);
        var nonempty = inputs.Where(x => x.Time.Length > 0).ToArray();
        if (nonempty.Length == 0) throw new ArgumentException("De gekozen signalen bevatten geen meetpunten.");
        var start = options.StartSeconds ?? nonempty.Min(x => x.Time[0]);
        var end = options.EndSeconds ?? nonempty.Max(x => x.Time[^1]);
        if (end <= start) throw new ArgumentException("De eindtijd moet na de starttijd liggen.");
        var mergedGaps = MergeGaps(gaps ?? []);
        var coverage = BuildSignalSpans(source, _ => true, start, end, options, mergedGaps, cancellationToken);
        var socSpans = soc is null ? [] : BuildSocSpans(soc, start, end, options, mergedGaps, cancellationToken);
        var detected = options.Method == ActivityDetectionMethod.SocSlope
            ? DetectSocActivity(socSpans, options, cancellationToken)
            : BuildSignalSpans(source, value => (options.Direction switch
                {
                    ActivitySignalDirection.Positive => value,
                    ActivitySignalDirection.Negative => -value,
                    _ => Math.Abs(value)
                }) > options.ActivityThreshold,
                start, end, options, mergedGaps, cancellationToken);
        if (options.Method == ActivityDetectionMethod.SocSlope)
            coverage = detected.Select(x => x with { Value = true }).ToList();
        var active = MergeActivity(detected, options);
        var power = on is null ? coverage : BuildSignalSpans(on, value => value > options.OnThreshold,
            start, end, options, mergedGaps, cancellationToken);

        // Sweep only state boundaries, not all samples or a wall-clock-sized resampling grid.
        var boundaries = new SortedSet<double> { start, end };
        foreach (var span in coverage.Concat(active).Concat(power))
        {
            boundaries.Add(span.Start);
            boundaries.Add(span.End);
        }
        var intervals = new List<UsageInterval>();
        var ci = 0;
        var ai = 0;
        var pi = 0;
        var previous = start;
        foreach (var boundary in boundaries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (boundary <= previous) continue;
            var time = previous + (boundary - previous) / 2;
            var isOn = Find(power, time, ref pi);
            var state = isOn switch
            {
                null => UsageState.Unknown,
                false => UsageState.Off,
                _ => Find(coverage, time, ref ci) is null ? UsageState.UnknownActivity
                    : Find(active, time, ref ai) == true ? UsageState.Active : UsageState.Idle
            };
            AddInterval(intervals, previous, boundary, state);
            previous = boundary;
        }

        var seconds = new double[5];
        var discharge = new double[5];
        var rise = new double[5];
        var intervalIndex = 0;
        foreach (var span in socSpans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (intervalIndex < intervals.Count && intervals[intervalIndex].EndSeconds <= span.Start) intervalIndex++;
            for (var i = intervalIndex; i < intervals.Count && intervals[i].StartSeconds < span.End; i++)
            {
                var from = Math.Max(span.Start, intervals[i].StartSeconds);
                var to = Math.Min(span.End, intervals[i].EndSeconds);
                if (to <= from) continue;
                var state = (int)intervals[i].State;
                var drop = span.At(from) - span.At(to);
                seconds[state] += to - from;
                discharge[state] += Math.Max(0, drop);
                rise[state] += Math.Max(0, -drop);
            }
        }
        return new ActiveUsageResult
        {
            Options = options, StartSeconds = start, EndSeconds = end, HasOnSignal = on is not null,
            Intervals = intervals,
            StartSoc = socSpans.Count > 0 ? socSpans[0].First : null,
            EndSoc = socSpans.Count > 0 ? socSpans[^1].Last : null,
            StartSocTime = socSpans.Count > 0 ? socSpans[0].Start : null,
            EndSocTime = socSpans.Count > 0 ? socSpans[^1].End : null,
            EnergyByState = Enum.GetValues<UsageState>().ToDictionary(x => x,
                x => new UsageEnergy(seconds[(int)x], discharge[(int)x], rise[(int)x], options.BatteryCapacityKwh))
        };
    }

    private static void ValidateOptions(ActiveUsageOptions o)
    {
        if (!Enum.IsDefined(o.Method) || !Enum.IsDefined(o.Direction) ||
            !double.IsFinite(o.BatteryCapacityKwh) || o.BatteryCapacityKwh <= 0 ||
            !double.IsFinite(o.ReserveSocPercent) || o.ReserveSocPercent is < 0 or >= 100 ||
            !double.IsFinite(o.ActivityThreshold) || o.ActivityThreshold < 0 || !double.IsFinite(o.OnThreshold) ||
            !double.IsFinite(o.BridgePauseSeconds) || o.BridgePauseSeconds < 0 ||
            !double.IsFinite(o.MinimumActivitySeconds) || o.MinimumActivitySeconds < 0 ||
            !double.IsFinite(o.MaximumSampleGapSeconds) || o.MaximumSampleGapSeconds <= 0 ||
            !double.IsFinite(o.SocWindowSeconds) || o.SocWindowSeconds <= 0 ||
            !double.IsFinite(o.SocDropPerHourThreshold) || o.SocDropPerHourThreshold <= 0 ||
            (o.StartSeconds.HasValue && !double.IsFinite(o.StartSeconds.Value)) ||
            (o.EndSeconds.HasValue && !double.IsFinite(o.EndSeconds.Value)))
            throw new ArgumentException("Gebruik geldige getallen: capaciteit en tijdvensters groter dan nul, reserve 0–99%, overige grenzen niet negatief.");
    }

    private static void ValidateSeries(SignalSeries series, CancellationToken token)
    {
        var times = series.Time;
        var values = series.Value;
        for (var i = 0; i < times.Length; i++)
        {
            if ((i & 8191) == 0) token.ThrowIfCancellationRequested();
            if (!double.IsFinite(times[i]) || (i > 0 && (times[i] < times[i - 1] ||
                (times[i] == times[i - 1] && !values[i].Equals(values[i - 1])))))
                throw new ArgumentException($"Signaal {series.Label}: aflopende tijd of tegenstrijdige waarden op dezelfde tijd. Herstel eerst de brondata.");
        }
    }

    private static List<MeasurementGap> MergeGaps(IReadOnlyList<MeasurementGap> gaps)
    {
        var result = new List<MeasurementGap>();
        foreach (var gap in gaps.Where(g => double.IsFinite(g.StartSeconds) && double.IsFinite(g.EndSeconds) && g.EndSeconds > g.StartSeconds)
                     .OrderBy(g => g.StartSeconds))
        {
            if (result.Count > 0 && gap.StartSeconds <= result[^1].EndSeconds)
                result[^1] = result[^1] with { EndSeconds = Math.Max(result[^1].EndSeconds, gap.EndSeconds) };
            else result.Add(gap);
        }
        return result;
    }

    private static bool ValidPair(double from, double to, double first, double last,
        ActiveUsageOptions options, List<MeasurementGap> gaps, ref int gapIndex)
    {
        while (gapIndex < gaps.Count && gaps[gapIndex].EndSeconds <= from) gapIndex++;
        return to > from && to - from <= options.MaximumSampleGapSeconds && double.IsFinite(first) && double.IsFinite(last)
            && (gapIndex >= gaps.Count || gaps[gapIndex].StartSeconds >= to);
    }

    private static List<Span> BuildSignalSpans(SignalSeries series, Func<double, bool> predicate, double start, double end,
        ActiveUsageOptions options, List<MeasurementGap> gaps, CancellationToken token)
    {
        var result = new List<Span>();
        var gapIndex = 0;
        var times = series.Time;
        var values = series.Value;
        for (var i = Math.Max(0, LowerBound(times, start) - 1); i + 1 < times.Length && times[i] < end; i++)
        {
            if ((i & 8191) == 0) token.ThrowIfCancellationRequested();
            if (!ValidPair(times[i], times[i + 1], values[i], values[i + 1], options, gaps, ref gapIndex)) continue;
            AddSpan(result, Math.Max(start, times[i]), Math.Min(end, times[i + 1]), predicate(values[i]));
        }
        return result;
    }

    private static List<SocSpan> BuildSocSpans(SignalSeries series, double start, double end,
        ActiveUsageOptions options, List<MeasurementGap> gaps, CancellationToken token)
    {
        var result = new List<SocSpan>();
        var gapIndex = 0;
        var times = series.Time;
        var values = series.Value;
        for (var i = Math.Max(0, LowerBound(times, start) - 1); i + 1 < times.Length && times[i] < end; i++)
        {
            if ((i & 8191) == 0) token.ThrowIfCancellationRequested();
            if (!ValidPair(times[i], times[i + 1], values[i], values[i + 1], options, gaps, ref gapIndex) ||
                values[i] is < 0 or > 100 || values[i + 1] is < 0 or > 100) continue;
            var original = new SocSpan(times[i], times[i + 1], values[i], values[i + 1]);
            var from = Math.Max(start, original.Start);
            var to = Math.Min(end, original.End);
            if (to <= from) continue;
            var span = new SocSpan(from, to, original.At(from), original.At(to));
            // BMS signals often repeat a quantized value at high rates. Compact plateaus
            // without discarding any SOC changes or joining separate coverage intervals.
            if (result.Count > 0 && result[^1].End == from && result[^1].First == span.First &&
                result[^1].Last == span.First && span.First == span.Last)
                result[^1] = result[^1] with { End = to };
            else result.Add(span);
        }
        return result;
    }

    private static List<Span> DetectSocActivity(List<SocSpan> soc, ActiveUsageOptions options, CancellationToken token)
    {
        var result = new List<Span>();
        for (var first = 0; first < soc.Count;)
        {
            var last = first;
            while (last + 1 < soc.Count && soc[last].End == soc[last + 1].Start) last++;
            var cursor = first;
            for (var start = soc[first].Start; start < soc[last].End; start += options.SocWindowSeconds)
            {
                token.ThrowIfCancellationRequested();
                var end = Math.Min(start + options.SocWindowSeconds, soc[last].End);
                while (cursor < last && soc[cursor].End <= start) cursor++;
                var startSoc = soc[cursor].At(start);
                while (cursor < last && soc[cursor].End < end) cursor++;
                // Very short fragments cannot establish a reliable SOC trend.
                if (end - start < Math.Min(10, options.SocWindowSeconds)) continue;
                var rate = (startSoc - soc[cursor].At(end)) * 3600 / (end - start);
                AddSpan(result, start, end, rate >= options.SocDropPerHourThreshold);
            }
            first = last + 1;
        }
        return result;
    }

    private static List<Span> MergeActivity(List<Span> detected, ActiveUsageOptions options)
    {
        var result = new List<Span>();
        for (var i = 0; i < detected.Count; i++)
        {
            if (!detected[i].Value) continue;
            var span = detected[i];
            // Bridge only a known inactive pause, never missing samples, and do not extend bursts.
            while (i + 2 < detected.Count && !detected[i + 1].Value && detected[i + 2].Value &&
                detected[i + 1].Start == span.End && detected[i + 1].End == detected[i + 2].Start &&
                detected[i + 1].End - detected[i + 1].Start <= options.BridgePauseSeconds)
            {
                span = span with { End = detected[i + 2].End };
                i += 2;
            }
            if (span.End - span.Start >= options.MinimumActivitySeconds) AddSpan(result, span.Start, span.End, true);
        }
        return result;
    }

    private static bool? Find(List<Span> spans, double time, ref int index)
    {
        while (index < spans.Count && spans[index].End <= time) index++;
        return index < spans.Count && spans[index].Start <= time ? spans[index].Value : null;
    }

    private static int LowerBound(double[] values, double value)
    {
        var lo = 0;
        var hi = values.Length;
        while (lo < hi) { var mid = lo + (hi - lo) / 2; if (values[mid] < value) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static void AddSpan(List<Span> spans, double start, double end, bool value)
    {
        if (end <= start) return;
        if (spans.Count > 0 && spans[^1].End == start && spans[^1].Value == value)
            spans[^1] = spans[^1] with { End = end };
        else spans.Add(new Span(start, end, value));
    }

    private static void AddInterval(List<UsageInterval> intervals, double start, double end, UsageState state)
    {
        if (intervals.Count > 0 && intervals[^1].EndSeconds == start && intervals[^1].State == state)
            intervals[^1] = intervals[^1] with { EndSeconds = end };
        else intervals.Add(new UsageInterval(start, end, state));
    }
}
