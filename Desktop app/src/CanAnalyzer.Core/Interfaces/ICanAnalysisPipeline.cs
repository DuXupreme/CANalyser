using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// End-to-end analysis pipeline: parse log, load DBC, decode, and build cache.
/// </summary>
public interface ICanAnalysisPipeline
{
    /// <summary>Processes once, retaining valid data for explicit review before it can be used.</summary>
    Task<PreparedCanAnalysis> PrepareForReviewAsync(
        string logFilePath, string dbcFilePath, IProgress<LoadProgress>? progress, CancellationToken cancellationToken);

    Task<CanDataset> LoadAsync(
        string logFilePath,
        string dbcFilePath,
        ImportMode importMode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
