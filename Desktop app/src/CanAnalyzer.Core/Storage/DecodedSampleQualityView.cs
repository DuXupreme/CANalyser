using System.Collections;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.Core.Storage;

internal sealed class DecodedSampleQualityView(
    IReadOnlyList<DecodedSignalSample> source,
    DecodeQuality quality) : IReadOnlyList<DecodedSignalSample>, IFrameSampleLookup, ISignalSampleLookup, IDisposable
{
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
        var samples = source.Where(sample => sample.FrameIndex == frameIndex).ToArray();
        messageName = samples.FirstOrDefault()?.MessageName ?? string.Empty;
        sampleCount = samples.Length;
        return sampleCount > 0;
    }

    public IReadOnlyList<DecodedSignalSample> GetFrameSamples(long frameIndex) =>
        source is IFrameSampleLookup lookup
            ? lookup.GetFrameSamples(frameIndex).Select(sample => sample with { Quality = quality }).ToArray()
            : source.Where(sample => sample.FrameIndex == frameIndex).Select(sample => sample with { Quality = quality }).ToArray();

    public void Dispose() => (source as IDisposable)?.Dispose();
}
