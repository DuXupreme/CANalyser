using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Decoding;

/// <summary>
/// Parsed DBC message with normalized frame ID and signal list.
/// </summary>
public sealed class DbcMessage
{
    public required uint RawFrameId { get; init; }

    public required bool IsExtendedFrame { get; init; }

    public required string Name { get; init; }

    public required int Dlc { get; init; }

    public List<DbcSignal> Signals { get; } = [];

    public uint NormalizedFrameId => CanIdUtilities.NormalizeDbcFrameId(RawFrameId, IsExtendedFrame);
}
