using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Selects and runs an appropriate parser for the log file.
/// </summary>
public interface ICanLogParsingService
{
    Task<IReadOnlyList<RawCanFrame>> ParseAsync(
        string filePath,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
