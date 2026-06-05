namespace CanAnalyzer.Core.Decoding;

/// <summary>
/// In-memory DBC database.
/// </summary>
public sealed class DbcDatabase
{
    public required IReadOnlyList<DbcMessage> Messages { get; init; }
}
