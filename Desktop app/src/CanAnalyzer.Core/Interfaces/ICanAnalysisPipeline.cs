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
        ImportMode importMode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
