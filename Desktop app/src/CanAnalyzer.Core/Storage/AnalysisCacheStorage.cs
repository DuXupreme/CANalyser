using System.Text;

namespace CanAnalyzer.Core.Storage;

/// <summary>Accounts for temporary analysis data and removes only abandoned, explicitly owned sessions.</summary>
public static class AnalysisCacheStorage
{
    internal const string OwnerFileName = "owner.lock";
    internal const string OwnerSignature = "CANalyser temporary analysis session v1\n";
    private const string SessionPrefix = "session-";
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "CANalyser");

    /// <summary>The owner handle stays open while data files are closed between lazy reads.</summary>
    public static AnalysisCacheSession CreateSession(string directory)
    {
        var root = Path.GetFullPath(directory);
        EnsureNoLinks(root);
        Directory.CreateDirectory(root);
        EnsureNoLinks(root);
        var session = Path.Combine(root, SessionPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var owner = new FileStream(Path.Combine(session, OwnerFileName), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.Read);
        try
        {
            owner.Write(Encoding.UTF8.GetBytes(OwnerSignature));
            owner.Flush(flushToDisk: true);
            return new AnalysisCacheSession(session, owner);
        }
        catch { owner.Dispose(); throw; }
    }

    public static CacheUsage Inspect(string directory, bool recursive = false)
    {
        long bytes = 0;
        var files = 0;
        EnsureNoLinks(directory);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            }))
            {
                if (Path.GetFileName(path) == OwnerFileName) continue;
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

    /// <summary>
    /// Flat legacy caches are preserved: old apps have no ownership lock, and a closed
    /// data handle does not prove that their lazy dataset is unused.
    /// </summary>
    public static AnalysisCacheCleanupResult ClearUnused(string directory, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        EnsureNoLinks(root);
        if (!Directory.Exists(root)) return new(new(0, 0, 0), 0, 0, 0, 0);
        long deletedBytes = 0;
        var deletedSessions = 0;
        var busySessions = 0;
        var unownedEntries = 0;
        var rejectedSessions = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (!name.StartsWith(SessionPrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name[SessionPrefix.Length..], "N", out _))
            {
                unownedEntries++;
                continue;
            }
            var status = TryClearSession(entry, out var removed, cancellationToken);
            deletedBytes += removed;
            switch (status)
            {
                case CleanupStatus.Deleted: deletedSessions++; break;
                case CleanupStatus.Busy: busySessions++; break;
                case CleanupStatus.Unowned: unownedEntries++; break;
                case CleanupStatus.Unsafe: rejectedSessions++; break;
            }
        }
        var remaining = Inspect(root, recursive: true);
        return new(remaining with { DeletedBytes = deletedBytes }, deletedSessions, busySessions, unownedEntries, rejectedSessions);
    }

    internal static void ReleaseSession(string directory)
    {
        try { TryClearSession(directory, out _, CancellationToken.None); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private enum CleanupStatus { Deleted, Busy, Unowned, Unsafe }

    private static CleanupStatus TryClearSession(string directory, out long deletedBytes, CancellationToken token)
    {
        deletedBytes = 0;
        try
        {
            EnsureNoLinks(directory);
            if (!Directory.Exists(directory)) return CleanupStatus.Deleted;
            var ownerPath = Path.Combine(directory, OwnerFileName);
            if (!File.Exists(ownerPath)) return CleanupStatus.Unowned;
            EnsureNoLinks(ownerPath);
            // A competing cleaner or a live owner in any process cannot acquire this handle.
            using (var owner = new FileStream(ownerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var signature = Encoding.UTF8.GetBytes(OwnerSignature);
                if (owner.Length != signature.Length) return CleanupStatus.Unowned;
                var actual = new byte[signature.Length];
                owner.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(signature)) return CleanupStatus.Unowned;
                // Validate the whole tree before deleting anything. Never traverse a link/junction.
                var files = new List<string>();
                var directories = new List<string>();
                CollectSafeTree(directory, files, directories, token);
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.Equals(file, ownerPath, StringComparison.OrdinalIgnoreCase)) continue;
                    EnsureNoLinks(file);
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    deletedBytes += length;
                }
                foreach (var child in directories.AsEnumerable().Reverse())
                {
                    EnsureNoLinks(child);
                    Directory.Delete(child); // Nonrecursive: a new file or link prevents removal.
                }
            }
            File.Delete(ownerPath);
            Directory.Delete(directory);
            return CleanupStatus.Deleted;
        }
        catch (UnsafeCachePathException) { return CleanupStatus.Unsafe; }
        catch (DirectoryNotFoundException) { return CleanupStatus.Deleted; }
        catch (FileNotFoundException) { return CleanupStatus.Busy; }
        catch (IOException) { return CleanupStatus.Busy; }
        catch (UnauthorizedAccessException) { return CleanupStatus.Busy; }
    }

    private static void CollectSafeTree(string directory, List<string> files, List<string> directories, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            EnsureNoLinks(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                directories.Add(path);
                CollectSafeTree(path, files, directories, token);
            }
            else files.Add(path);
        }
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnsafeCachePathException("De analyseopslag bevat een bestandsverwijzing. Deze opslag wordt niet opgeruimd.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class UnsafeCachePathException(string message) : IOException(message);
}

public sealed record AnalysisCacheCleanupResult(CacheUsage Usage, int DeletedSessions, int BusySessions,
    int UnownedEntries, int RejectedSessions);

public sealed class AnalysisCacheSession : IDisposable
{
    private FileStream? _owner;
    internal AnalysisCacheSession(string directory, FileStream owner) { DirectoryPath = directory; _owner = owner; }
    public string DirectoryPath { get; }
    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null) return;
        owner.Dispose();
        AnalysisCacheStorage.ReleaseSession(DirectoryPath);
    }
}
