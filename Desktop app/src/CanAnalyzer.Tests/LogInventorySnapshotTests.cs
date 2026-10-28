using System.Text.Json;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class LogInventorySnapshotTests
{
    [Fact]
    public async Task HistoricalInventoryRoundTripsAndNeverProvesCurrentQueueEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var time = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var hash = new string('A', 64);
        try
        {
            var snapshot = new LogInventorySnapshot { InventoryTakenAtUtc = time,
                Files = [new("48EDFD35", "00000001", "00000001.MF4", 100, hash)] };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, LogInventorySnapshotCodec.Options));
            var loaded = await LogInventorySnapshotCodec.ReadAsync(path);
            var status = LogInventoryReconciler.Reconcile(loaded.InventoryTakenAtUtc, loaded.Files,
                [new("48EDFD35", "00000001", "00000001.MF4", 100, hash, time.AddDays(2))]);
            Assert.True(status.InventoryFullyReceived);
            Assert.Equal(time, status.InventoryTakenAtUtc);
            Assert.Null(status.CurrentQueueBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SnapshotWithoutObservationDateOrWithUnhashedContentsIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => LogInventorySnapshotCodec.Validate(new()));
        Assert.Throws<InvalidDataException>(() => LogInventorySnapshotCodec.Validate(new()
        {
            InventoryTakenAtUtc = DateTimeOffset.UtcNow,
            Files = [new("48EDFD35", "00000001", "00000001.MF4", 100, "")]
        }));
    }

    [Fact]
    public void MatchingContentWithoutAnObservedServerReceiptIsNotConfirmed()
    {
        var inventoryTime = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var hash = new string('A', 64);
        LogInventoryEntry[] inventory = [new("48EDFD35", "00000001", "00000001.MF4", 100, hash)];
        foreach (var receipt in new DateTimeOffset?[] { null, default(DateTimeOffset) })
        {
            var status = LogInventoryReconciler.Reconcile(inventoryTime, inventory,
                [new("48EDFD35", "00000001", "00000001.MF4", 100, hash, receipt)]);
            Assert.Equal(0, status.ConfirmedReceivedFiles);
            Assert.Equal(0, status.ConfirmedReceivedBytes);
            Assert.Single(status.UnconfirmedFiles);
            Assert.Null(status.LastServerReceiptUtc);
            Assert.Null(status.CurrentQueueBytes);
        }
        var received = LogInventoryReconciler.Reconcile(inventoryTime, inventory,
            [new("48EDFD35", "00000001", "00000001.MF4", 100, hash, inventoryTime.AddHours(1))]);
        Assert.Equal(1, received.ConfirmedReceivedFiles);
        Assert.Equal(100, received.ConfirmedReceivedBytes);
        Assert.Null(received.CurrentQueueBytes);
    }
}
