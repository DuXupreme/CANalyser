namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Complete in-memory dataset used by UI features (analysis + raw frame view + export).
/// </summary>
public sealed class CanDataset
{
    public required IReadOnlyList<RawCanFrame> RawFrames { get; init; }

    public required IReadOnlyList<DecodedSignalSample> DecodedSamples { get; init; }

    public required IReadOnlyList<MessageSummary> MessageSummaries { get; init; }

    public required IReadOnlyDictionary<string, SignalSeries> SignalSeriesByLabel { get; init; }

    public required IReadOnlyList<string> SignalLabels { get; init; }

    public required DecoderDiagnostics Diagnostics { get; init; }

    public int RawCount => RawFrames.Count;

    public int SignalCount => SignalLabels.Count;

    public int ExtendedCount => RawFrames.Count(frame => frame.IsExtended);
}
