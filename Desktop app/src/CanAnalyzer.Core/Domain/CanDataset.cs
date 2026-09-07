using CanAnalyzer.Core.Storage;

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

    /// <summary>Absolute UTC start time supplied by the logger, when available.</summary>
    public DateTimeOffset? StartTimeUtc { get; init; }

    public LoadTimings? LoadTimings { get; internal set; }

    public string SourceLogPath { get; internal set; } = string.Empty;
    public string SourceDbcPath { get; internal set; } = string.Empty;
    public IReadOnlyList<SourceLogFile> SourceFiles { get; internal set; } = [];

    public int RawCount => RawFrames.Count;

    public int SignalCount => SignalLabels.Count;

    private int? _extendedCount;
    public int ExtendedCount => _extendedCount ??= RawFrames is DiskBackedFrameStore store
        ? store.ExtendedCount : RawFrames.Count(frame => frame.IsExtended);

    public IReadOnlyCollection<string> Channels => RawFrames is DiskBackedFrameStore store
        ? store.Channels : RawFrames.Select(frame => frame.Channel).Distinct(StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        (RawFrames as IDisposable)?.Dispose();
        (DecodedSamples as IDisposable)?.Dispose();
    }
}
