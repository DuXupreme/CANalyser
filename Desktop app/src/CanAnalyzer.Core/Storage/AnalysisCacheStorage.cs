namespace CanAnalyzer.Core.Storage;

/// <summary>Read-only accounting for temporary analysis data, including active files.</summary>
public static class AnalysisCacheStorage
{
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "CANalyser");

    public static CacheUsage Inspect(string directory, bool recursive = false)
    {
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            }))
            {
                try
                {
                    var info = new FileInfo(path);
                    bytes += info.Length;
                    files++;
                }
                catch (FileNotFoundException) { } // A dataset may close while counting.
            }
        }
        catch (DirectoryNotFoundException) { }
        return new(bytes, files, 0);
    }
}
