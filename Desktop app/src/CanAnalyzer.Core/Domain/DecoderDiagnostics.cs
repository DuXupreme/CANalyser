namespace CanAnalyzer.Core.Domain;

public enum DecodeFailureKind
{
    DlcMismatch,
    SuppressedDefinition,
    FrameFormatMismatch,
    SignalExtraction
}

public sealed record DecodeFailureSummary(
    uint ObservedFrameId,
    bool IsExtended,
    uint? DbcFrameId,
    IReadOnlyList<string> MessageNames,
    DecodeFailureKind Kind,
    int ActualPayloadLength,
    IReadOnlyList<int> ExpectedPayloadLengths,
    int Count,
    string? FailingSignalName = null);

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
    int AmbiguousFrameCount = 0,
    IReadOnlyList<DecodeFailureSummary>? DecodeFailures = null);
