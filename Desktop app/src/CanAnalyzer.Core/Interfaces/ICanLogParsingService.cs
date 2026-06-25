using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Selects and runs an appropriate parser for the log file.
/// </summary>
public interface ICanLogParsingService
{
    Task<CanLogParseResult> ParseAsync(
        string filePath,
        ImportMode mode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
