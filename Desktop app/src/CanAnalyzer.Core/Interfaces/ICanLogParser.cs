using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Single log format parser.
/// </summary>
public interface ICanLogParser
{
    string Name { get; }

    Task<IReadOnlyList<RawCanFrame>?> ParseAsync(
        string filePath,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
