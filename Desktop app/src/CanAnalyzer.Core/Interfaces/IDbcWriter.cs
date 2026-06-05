using CanAnalyzer.Core.Decoding;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Serializes an in-memory <see cref="DbcDatabase"/> to a standard DBC text file.
/// </summary>
public interface IDbcWriter
{
    Task WriteAsync(DbcDatabase database, string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Produces the DBC file contents without writing to disk (used for previews and tests).
    /// </summary>
    string Serialize(DbcDatabase database);
}
