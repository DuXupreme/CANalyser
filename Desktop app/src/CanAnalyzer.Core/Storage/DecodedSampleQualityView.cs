using System.Collections;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.Core.Storage;

internal sealed class DecodedSampleQualityView(
    IReadOnlyList<DecodedSignalSample> source,
    DecodeQuality quality) : IReadOnlyList<DecodedSignalSample>, IFrameSampleLookup, IDisposable
{
    public int Count => source.Count;

    public DecodedSignalSample this[int index] => source[index] with { Quality = quality };

    public IEnumerator<DecodedSignalSample> GetEnumerator()
    {
        foreach (var sample in source) yield return sample with { Quality = quality };
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

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
