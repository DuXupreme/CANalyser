namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Aggregated message count for decoded frames.
/// </summary>
public sealed record MessageSummary(uint FrameId, string MessageName, int Count)
{
    public string FrameIdHex => $"0x{FrameId:X}";
}
