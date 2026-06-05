using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Decodes raw frames into signal samples using a DBC model.
/// </summary>
public interface ICanDecodingService
{
    DecodeResult Decode(
        IReadOnlyList<RawCanFrame> rawFrames,
        DbcDatabase database,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
