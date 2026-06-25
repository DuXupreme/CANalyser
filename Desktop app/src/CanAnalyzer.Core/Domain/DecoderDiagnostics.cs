namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Diagnostic counters and notes from DBC decode pass.
/// </summary>
public sealed record DecoderDiagnostics(
    int UnmatchedFrameCount,
    int UnmatchedUniqueIds,
    int DbcMessageCount,
    int ManualDecodeFrameCount,
    int ManualDecodeUniqueIds,
    string DecodeNote,
    int DecodeErrorFrameCount = 0,
    int AmbiguousFrameCount = 0);
