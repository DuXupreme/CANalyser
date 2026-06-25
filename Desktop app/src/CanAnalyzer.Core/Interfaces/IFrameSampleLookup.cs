using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>Compact lookup from a stable frame index to its decoded signal samples.</summary>
public interface IFrameSampleLookup
{
    bool TryGetFrameSummary(long frameIndex, out string messageName, out int sampleCount);

    IReadOnlyList<DecodedSignalSample> GetFrameSamples(long frameIndex);
}
