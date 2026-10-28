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
    public async Task FloatingSignalWatchPlacementAndPinRoundTripIndependentlyOfSignalSelection()
    {
        var signal = new SignalIdentity("2", CanFrameFormat.Classic, false, 42, "Actuator", "Position");
        await CreateStore().SaveAsync(new AppSettings
        {
            SignalWatch = new() { Signals = [signal], LatestValues = true },
            SignalWatchWindow = new() { AlwaysOnTop = false, Left = -1500, Top = 125, Width = 590, Height = 300 }
        }, CancellationToken.None);

        var reopened = CreateStore().Load();
        Assert.False(reopened.SignalWatchWindow.AlwaysOnTop);
        Assert.Equal(-1500, reopened.SignalWatchWindow.Left);
        Assert.Equal(125, reopened.SignalWatchWindow.Top);
        Assert.Equal(590, reopened.SignalWatchWindow.Width);
        Assert.Equal(300, reopened.SignalWatchWindow.Height);
        Assert.Equal(signal, Assert.Single(reopened.SignalWatch.Signals));
        Assert.True(reopened.SignalWatch.LatestValues);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"signalWatchWindow\":null}")]
    public void MissingOrNullFloatingSignalWatchSettingsUsePinnedCompactDefaults(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CreateStore().SettingsPath, json);
        var options = CreateStore().Load().SignalWatchWindow;
        Assert.True(options.AlwaysOnTop);
        Assert.Equal(640, options.Width);
        Assert.Equal(340, options.Height);
        Assert.Null(options.Left);
        Assert.Null(options.Top);
    }

    [Fact]
    public async Task ConcurrentSavesCannotPublishAnOlderProfileAfterTheNewerProfile()
    {
        var path = Path.Combine(_directory, "settings.json");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppSettingsStore WithWriter(Func<string, string, CancellationToken, Task> writer) =>
            (AppSettingsStore)Activator.CreateInstance(typeof(AppSettingsStore),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null, args: [path, writer], culture: null)!;
        var older = WithWriter(async (file, json, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(file, json, token);
        });
        var newer = WithWriter(async (file, json, token) =>
        {
            newerEntered.SetResult();
            await File.WriteAllTextAsync(file, json, token);
        });
        var first = older.SaveAsync(new AppSettings { AnalysisProfiles = [new() { Id = "machine", Version = 1 }] }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = newer.SaveAsync(new AppSettings { AnalysisProfiles = [new() { Id = "machine", Version = 2 }] }, default);
        try
        {
            await Task.Delay(100);
            Assert.False(newerEntered.Task.IsCompleted);
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, second); }
        Assert.Equal(2, Assert.Single(CreateStore().Load().AnalysisProfiles).Version);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

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
