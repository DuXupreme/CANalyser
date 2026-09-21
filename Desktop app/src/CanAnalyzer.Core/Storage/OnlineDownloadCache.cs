namespace CanAnalyzer.Core.Storage;

/// <summary>Manages only CANalyser's disposable online download archives.</summary>
public sealed class OnlineDownloadCache(string directory)
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CANalyser", "online-cache");

    public CacheUsage Inspect() => Scan(null, false);

    public CacheUsage Clear(string? preservedPath = null) => Scan(preservedPath, true);

    private CacheUsage Scan(string? preservedPath, bool delete)
    {
        long bytes = 0;
        long deletedBytes = 0;
        var count = 0;
        if (!Directory.Exists(directory)) return new(0, 0, 0);
        var root = Path.GetFullPath(directory);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("De cachemap is een verwijzing; opruimen is niet toegestaan.");
        var preserve = string.IsNullOrEmpty(preservedPath) || !Path.IsPathFullyQualified(preservedPath)
            ? null : Path.GetFullPath(preservedPath);
        foreach (var path in Directory.EnumerateFiles(root, "online-logs-*", SearchOption.TopDirectoryOnly))
        {
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".zip.partial", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var length = info.Length;
                if (delete && !string.Equals(path, preserve, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Exclusive access prevents removal during a download/import.
                        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                            FileShare.None, 4096, FileOptions.DeleteOnClose);
                        deletedBytes += length;
                        continue;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                bytes += length;
                count++;
            }
            catch (FileNotFoundException) { }
        }
        return new(bytes, count, deletedBytes);
    }
}

public sealed record CacheUsage(long Bytes, int Files, long DeletedBytes);
