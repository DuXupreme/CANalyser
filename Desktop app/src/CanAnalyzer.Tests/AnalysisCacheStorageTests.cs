using CanAnalyzer.Core.Storage;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class AnalysisCacheStorageTests
{
    [Fact]
    public void InspectCountsLockedFilesWithoutChangingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "active.samples");
            File.WriteAllBytes(path, new byte[123]);
            File.WriteAllBytes(Path.Combine(root, "old.index"), new byte[45]);
            using var active = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var usage = AnalysisCacheStorage.Inspect(root);
            Assert.Equal(168, usage.Bytes);
            Assert.Equal(2, usage.Files);
            Assert.Equal(0, usage.DeletedBytes);
            Assert.Equal(123, active.Length);
            Assert.True(File.Exists(Path.Combine(root, "old.index")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingCacheIsEmptyAndIsNotCreated()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Equal(new CacheUsage(0, 0, 0), AnalysisCacheStorage.Inspect(path));
        Assert.False(Directory.Exists(path));
    }
}