using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Decoding;

/// <summary>
/// In-memory DBC database.
/// </summary>
public sealed class DbcDatabase
{
    public required IReadOnlyList<DbcMessage> Messages { get; init; }

    public IReadOnlyList<ImportIssue> Issues { get; init; } = [];

    public bool IsLosslessWritable { get; init; } = true;
}
