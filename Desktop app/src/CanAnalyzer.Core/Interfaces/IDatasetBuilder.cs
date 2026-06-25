using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Builds cache-friendly dataset structures for UI workflows.
/// </summary>
public interface IDatasetBuilder
{
    CanDataset Build(
        IReadOnlyList<RawCanFrame> rawFrames,
        IReadOnlyList<DecodedSignalSample> decodedSamples,
        IReadOnlyList<MessageSummary> messageSummaries,
        DecoderDiagnostics diagnostics,
        ImportReport? importReport = null,
        DatasetCompleteness completeness = DatasetCompleteness.Complete,
        string sourceLogSha256 = "",
        string dbcSha256 = "",
        string applicationVersion = "");
}
