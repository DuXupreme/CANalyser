using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using System.Diagnostics;
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
        Assert.Equal(0, AnalysisCacheStorage.ClearUnused(path).Usage.DeletedBytes);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void CompletedLazyStoresKeepTheirOwnershipUntilDatasetReadersFinish()
    {
        var root = TemporaryRoot();
        try
        {
            var framesRoot = Path.Combine(root, "frame-cache");
            var samplesRoot = Path.Combine(root, "sample-cache");
            var frames = new DiskBackedFrameStore(framesRoot);
            frames.Append(new RawCanFrame(0, 0x123, 1, [42], "Rx", "1", false));
            frames.Complete();
            var samples = new DiskBackedDecodedSampleStore(samplesRoot);
            var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 0x123, "Test", "Value");
            samples.Append(new DecodedSignalSample(0, 0, 1, identity, 42, 42, "unit", DecodeQuality.Valid));
            samples.Complete(); // No payload file needs an open handle until the first lazy read.
            using var dataset = new DatasetBuilder().Build(frames, samples, [], new(0, 0, 0, 0, 0, ""));
            using var reader = dataset.AcquireReadLease();
            dataset.Dispose();
            Assert.Equal(1, AnalysisCacheStorage.ClearUnused(framesRoot).BusySessions);
            Assert.Equal(1, AnalysisCacheStorage.ClearUnused(samplesRoot).BusySessions);
            Assert.Equal(42, frames[0].Data[0]);
            Assert.Equal(42, samples.ReadSignalSeries(identity).Single().Value);
            reader.Dispose();
            Assert.Empty(Directory.EnumerateFileSystemEntries(framesRoot));
            Assert.Empty(Directory.EnumerateFileSystemEntries(samplesRoot));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AbandonedOwnedSessionIsRemovedWhileLegacyAndUserFilesArePreserved()
    {
        var root = TemporaryRoot();
        try
        {
            var session = AbandonedSession(root);
            Directory.CreateDirectory(Path.Combine(session, "output"));
            File.WriteAllBytes(Path.Combine(session, "output", "converted.trc"), new byte[123]);
            File.WriteAllBytes(Path.Combine(session, "decoded.samples"), new byte[45]);
            foreach (var name in new[] { "legacy.frames", "source.MF4", "database.dbc", "profile.json", "export.csv" })
                File.WriteAllText(Path.Combine(root, name), "keep");
            var unknownSession = Path.Combine(root, "previous-version");
            Directory.CreateDirectory(unknownSession);
            File.WriteAllText(Path.Combine(unknownSession, "source.MF4"), "keep");

            var result = AnalysisCacheStorage.ClearUnused(root);
            Assert.Equal(168, result.Usage.DeletedBytes);
            Assert.Equal(1, result.DeletedSessions);
            Assert.Equal(6, result.UnownedEntries);
            Assert.False(Directory.Exists(session));
            Assert.Equal(6, result.Usage.Files);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "database.dbc")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(unknownSession, "source.MF4")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingOrInvalidOwnershipMarkerCannotAuthorizeDeletion()
    {
        var root = TemporaryRoot();
        try
        {
            foreach (var marker in new[] { "", "other application" })
            {
                var session = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(session);
                if (marker.Length > 0) File.WriteAllText(Path.Combine(session, "owner.lock"), marker);
                File.WriteAllText(Path.Combine(session, "important.txt"), "keep");
            }
            var result = AnalysisCacheStorage.ClearUnused(root);
            Assert.Equal(0, result.Usage.DeletedBytes);
            Assert.Equal(2, result.UnownedEntries);
            Assert.Equal(2, Directory.EnumerateFiles(root, "important.txt", SearchOption.AllDirectories).Count());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AnotherProcessOwnerProtectsClosedPayloadUntilItExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = TemporaryRoot();
        Process? process = null;
        try
        {
            var session = AbandonedSession(root);
            File.WriteAllText(Path.Combine(session, "lazy.series"), "source");
            var script = Path.Combine(root, "hold-owner.ps1");
            await File.WriteAllTextAsync(script,
                "param([string]$OwnerPath)\n$owner = [IO.File]::Open($OwnerPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)\ntry { [Console]::WriteLine('ready'); [Console]::ReadLine() | Out-Null } finally { $owner.Dispose() }\n");
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-OwnerPath", Path.Combine(session, "owner.lock") })
                start.ArgumentList.Add(arg);
            process = Process.Start(start)!;
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            var busy = AnalysisCacheStorage.ClearUnused(root);
            Assert.Equal(1, busy.BusySessions);
            Assert.Equal("source", File.ReadAllText(Path.Combine(session, "lazy.series")));
            await process.StandardInput.WriteLineAsync("release");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(1, AnalysisCacheStorage.ClearUnused(root).DeletedSessions);
            Assert.False(Directory.Exists(session));
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            process?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LinkedSessionTreeIsRejectedBeforeAnyPayloadIsDeleted()
    {
        var root = TemporaryRoot();
        var outside = TemporaryRoot();
        string? link = null;
        try
        {
            File.WriteAllText(Path.Combine(outside, "source.MF4"), "keep outside");
            var session = AbandonedSession(root);
            File.WriteAllText(Path.Combine(session, "first.samples"), "keep inside");
            link = Path.Combine(session, "redirect");
            CreateDirectoryLink(link, outside);
            var result = AnalysisCacheStorage.ClearUnused(root);
            Assert.Equal(1, result.RejectedSessions);
            Assert.Equal(0, result.Usage.DeletedBytes);
            Assert.Equal("keep outside", File.ReadAllText(Path.Combine(outside, "source.MF4")));
            Assert.Equal("keep inside", File.ReadAllText(Path.Combine(session, "first.samples")));
            Assert.ThrowsAny<IOException>(() => AnalysisCacheStorage.ClearUnused(link));
            Assert.ThrowsAny<IOException>(() => AnalysisCacheStorage.CreateSession(Path.Combine(link, "cache")));
        }
        finally
        {
            if (link is not null && Directory.Exists(link)) Directory.Delete(link); // Remove only the link.
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "canalyser-storage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string AbandonedSession(string root)
    {
        var session = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "owner.lock"), "CANalyser temporary analysis session v1\n");
        return session;
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"", UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }
}
