using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Applies raw frame table filters.
/// </summary>
public interface IRawFrameFilterService
{
    IReadOnlyList<RawCanFrame> Apply(
        IReadOnlyList<RawCanFrame> frames,
        RawFrameFilterOptions options);
}
