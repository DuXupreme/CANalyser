using System.IO;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.State;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "canalyser-settings-tests-" + Guid.NewGuid().ToString("N"));

    private AppSettingsStore CreateStore() => new(Path.Combine(_directory, "settings.json"));

    [Fact]
    public async Task SaveAsync_ReplacesFileWithoutChangingAnExistingReadersSnapshot()
    {
        var store = CreateStore();
        await store.SaveAsync(new AppSettings { LastLogFilePath = "old.mf4" }, CancellationToken.None);
        var previous = await File.ReadAllBytesAsync(store.SettingsPath);

        // Readers that allow replacement must keep the old complete file, rather
        // than observe the original file being truncated and rewritten in place.
        await using (var reader = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete))
        {
            var signal = new SignalIdentity("can2", CanFrameFormat.Classic, false, 0x123, "Motor", "Position");
            var preset = new CsvExportPreset
            {
                Name = "Motorpositie",
                Options = new() { Signals = [signal], Columns = ["value", "signal_name", "time_s"], Delimiter = ";" }
            };
            await store.SaveAsync(new AppSettings
            {
                LastLogFilePath = "new.mf4",
                CsvExportPresets = [preset]
            }, CancellationToken.None);

            using var snapshot = new MemoryStream();
            await reader.CopyToAsync(snapshot);
            Assert.Equal(previous, snapshot.ToArray());
            var loaded = store.Load();
            Assert.Equal("new.mf4", loaded.LastLogFilePath);
            var restored = Assert.Single(loaded.CsvExportPresets);
            Assert.Equal(preset.Id, restored.Id);
            Assert.Equal(preset.Name, restored.Name);
            Assert.Equal(signal, Assert.Single(restored.Options.Signals!));
            Assert.Equal(preset.Options.Columns, restored.Options.Columns);
            Assert.Equal(";", restored.Options.Delimiter);
        }

        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_WhenReplacementIsBlocked_PreservesPreviousSettingsAndRemovesTemporaryFile()
    {
        var store = CreateStore();
        var preset = new CsvExportPreset { Name = "Bewaard" };
        await store.SaveAsync(new AppSettings { LastLogFilePath = "old.mf4", CsvExportPresets = [preset] }, CancellationToken.None);
        var previous = await File.ReadAllBytesAsync(store.SettingsPath);

        // Windows denies replacement while the target is open without Delete sharing.
        await using (var locked = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => store.SaveAsync(new AppSettings
            {
                LastLogFilePath = "new.mf4",
                CsvExportPresets = [new CsvExportPreset { Name = "Niet opgeslagen" }]
            }, CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected a denied replacement, received {error?.GetType().Name ?? "no exception"}.");
        }

        Assert.Equal(previous, await File.ReadAllBytesAsync(store.SettingsPath));
        Assert.Equal(preset.Id, Assert.Single(store.Load().CsvExportPresets).Id);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_WhenCanceled_PreservesPreviousSettings()
    {
        var store = CreateStore();
        await store.SaveAsync(new AppSettings { LastLogFilePath = "old.mf4" }, CancellationToken.None);
        var previous = await File.ReadAllBytesAsync(store.SettingsPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(new AppSettings { LastLogFilePath = "new.mf4" }, cancellation.Token));

        Assert.Equal(previous, await File.ReadAllBytesAsync(store.SettingsPath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
