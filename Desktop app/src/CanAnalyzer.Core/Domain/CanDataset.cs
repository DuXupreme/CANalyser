namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Traceable dataset backed by append-only stores; selected signal series materialize lazily.
/// </summary>
public sealed class CanDataset : IDisposable
{
    public required IReadOnlyList<RawCanFrame> RawFrames { get; init; }

    public required IReadOnlyList<DecodedSignalSample> DecodedSamples { get; init; }

    public required IReadOnlyList<MessageSummary> MessageSummaries { get; init; }

    public required IReadOnlyDictionary<string, SignalSeries> SignalSeriesByLabel { get; init; }

    public required IReadOnlyDictionary<SignalIdentity, SignalSeries> SignalSeriesByIdentity { get; init; }

    public required IReadOnlyList<string> SignalLabels { get; init; }

    public required DecoderDiagnostics Diagnostics { get; init; }

    public ImportReport? ImportReport { get; init; }

    public DatasetCompleteness Completeness { get; init; } = DatasetCompleteness.Complete;

    public string SourceLogSha256 { get; init; } = string.Empty;

    public string DbcSha256 { get; init; } = string.Empty;

    public string ApplicationVersion { get; init; } = string.Empty;

    public int RawCount => RawFrames.Count;

    public int SignalCount => SignalLabels.Count;

    public int ExtendedCount => RawFrames.Count(frame => frame.IsExtended);

    public void Dispose()
    {
        (RawFrames as IDisposable)?.Dispose();
        (DecodedSamples as IDisposable)?.Dispose();
    }
}
