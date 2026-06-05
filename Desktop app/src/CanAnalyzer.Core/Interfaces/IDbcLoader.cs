using CanAnalyzer.Core.Decoding;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Loads and parses DBC files.
/// </summary>
public interface IDbcLoader
{
    Task<DbcDatabase> LoadAsync(string filePath, CancellationToken cancellationToken);
}
