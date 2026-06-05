namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Output of the DBC decode pipeline.
/// </summary>
public sealed record DecodeResult(
    IReadOnlyList<DecodedSignalSample> Samples,
    IReadOnlyList<MessageSummary> MessageSummaries,
    DecoderDiagnostics Diagnostics);
