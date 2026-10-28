using System.Collections;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.Core.Storage;

internal sealed class DecodedSampleQualityView(
    IReadOnlyList<DecodedSignalSample> source,
    DecodeQuality quality) : IReadOnlyList<DecodedSignalSample>, IFrameSampleLookup, ISignalSampleLookup, IDisposable
{
    private readonly Lazy<Dictionary<SignalIdentity, SignalSeriesPoint[]>> _fallbackSignals = new(() =>
        source.GroupBy(sample => sample.Identity).ToDictionary(group => group.Key,
            group => group.OrderBy(sample => sample.TimestampNanoseconds).ThenBy(sample => sample.FrameIndex)
                .Select(sample => new SignalSeriesPoint(sample.TimestampNanoseconds, sample.FrameIndex, sample.Value)).ToArray()));
    private readonly Lazy<Dictionary<long, DecodedSignalSample[]>> _fallbackFrames = new(() =>
        source.GroupBy(sample => sample.FrameIndex).ToDictionary(group => group.Key, group => group.ToArray()));
    public int Count => source.Count;

    public DecodedSignalSample this[int index] => source[index] with { Quality = quality };

    public IEnumerator<DecodedSignalSample> GetEnumerator()
    {
        foreach (var sample in source) yield return sample with { Quality = quality };
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IReadOnlyList<SignalSampleSummary> GetSignalSummaries() =>
        source is ISignalSampleLookup lookup
            ? lookup.GetSignalSummaries().Select(summary => summary with { Latest = summary.Latest with { Quality = quality } }).ToArray()
            : source.GroupBy(sample => sample.Identity).Select(group => new SignalSampleSummary(
                group.Count(), group.Min(sample => sample.Value), group.Max(sample => sample.Value),
                group.OrderBy(sample => sample.TimestampNanoseconds).ThenBy(sample => sample.FrameIndex).Last() with { Quality = quality })).ToArray();

    public IEnumerable<SignalSeriesPoint> ReadSignalSeries(SignalIdentity identity) =>
        source is ISignalSampleLookup lookup
            ? lookup.ReadSignalSeries(identity)
            : source.Where(sample => sample.Identity == identity)
                .Select(sample => new SignalSeriesPoint(sample.TimestampNanoseconds, sample.FrameIndex, sample.Value));

    public SignalSeriesPoint? FindSignalPointAtOrBefore(SignalIdentity identity, long timestampNanoseconds)
    {
        if (source is ISignalSampleLookup lookup) return lookup.FindSignalPointAtOrBefore(identity, timestampNanoseconds);
        if (!_fallbackSignals.Value.TryGetValue(identity, out var samples)) return null;
        var low = 0; var high = samples.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].TimestampNanoseconds <= timestampNanoseconds) low = middle + 1; else high = middle;
        }
        return low == 0 ? null : samples[low - 1];
    }

    public IEnumerable<SignalSeriesPoint> ReadSignalSeriesSampled(SignalIdentity identity, int maximumPoints)
    {
        if (source is ISignalSampleLookup lookup)
        {
            foreach (var point in lookup.ReadSignalSeriesSampled(identity, maximumPoints)) yield return point;
            yield break;
        }

        var matching = source.Where(sample => sample.Identity == identity).ToArray();
        var count = Math.Min(maximumPoints, matching.Length);
        for (var index = 0; index < count; index++)
        {
            var sourceIndex = count == 1 ? 0 : (long)index * (matching.Length - 1L) / (count - 1L);
            var sample = matching[sourceIndex];
            yield return new SignalSeriesPoint(sample.TimestampNanoseconds, sample.FrameIndex, sample.Value);
        }
    }

    public bool TryGetFrameSummary(long frameIndex, out string messageName, out int sampleCount)
    {
        if (source is IFrameSampleLookup lookup)
            return lookup.TryGetFrameSummary(frameIndex, out messageName, out sampleCount);
        var samples = _fallbackFrames.Value.GetValueOrDefault(frameIndex) ?? [];
        messageName = samples.FirstOrDefault()?.MessageName ?? string.Empty;
        sampleCount = samples.Length;
        return sampleCount > 0;
    }

    public IReadOnlyList<DecodedSignalSample> GetFrameSamples(long frameIndex) =>
        source is IFrameSampleLookup lookup
            ? lookup.GetFrameSamples(frameIndex).Select(sample => sample with { Quality = quality }).ToArray()
            : (_fallbackFrames.Value.GetValueOrDefault(frameIndex) ?? []).Select(sample => sample with { Quality = quality }).ToArray();

    public void Dispose() => (source as IDisposable)?.Dispose();
}
