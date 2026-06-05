using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// End-to-end analysis pipeline: parse log, load DBC, decode, and build cache.
/// </summary>
public interface ICanAnalysisPipeline
{
    Task<CanDataset> LoadAsync(
        string logFilePath,
        string dbcFilePath,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
