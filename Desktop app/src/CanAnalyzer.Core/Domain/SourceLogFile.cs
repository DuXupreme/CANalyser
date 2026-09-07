namespace CanAnalyzer.Core.Domain;

/// <summary>Original source identity, retained independently of temporary conversion/cache filenames.</summary>
public sealed record SourceLogFile(string Name, string SourcePath, string? Logger, string? Session)
{
    public static SourceLogFile FromArchiveEntry(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new(parts.LastOrDefault() ?? entryPath, normalized,
            parts.Length >= 3 ? parts[^3] : null,
            parts.Length >= 3 ? parts[^2] : null);
    }

    public static SourceLogFile FromLocalFile(string path)
    {
        var source = FromArchiveEntry(path);
        // Only infer logger/session from a recognizable CANedge directory layout.
        var recognizable = Path.GetExtension(path).Equals(".mf4", StringComparison.OrdinalIgnoreCase) &&
                           source.Logger is { Length: 8 } logger && logger.All(Uri.IsHexDigit);
        return recognizable ? source : source with { Logger = null, Session = null };
    }
}
