using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Single log format parser.
/// </summary>
public interface ICanLogParser
{
    string Name { get; }

    int Probe(string filePath, IReadOnlyList<string> sampleLines);

    Task<CanLogParseResult?> ParseAsync(
        string filePath,
        ImportMode mode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
