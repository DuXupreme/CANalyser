using System.Text.Json;

namespace CanAnalyzer.Core.Domain;

public sealed record LogInventorySnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset InventoryTakenAtUtc { get; init; }
    public IReadOnlyList<LogInventoryEntry> Files { get; init; } = [];
}

/// <summary>An explicit historical snapshot, never evidence of the current upload queue.</summary>
public static class LogInventorySnapshotCodec
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<LogInventorySnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (input.Length > MaximumBytes) throw new InvalidDataException("De kaartinventaris is te groot.");
        var snapshot = await JsonSerializer.DeserializeAsync<LogInventorySnapshot>(input, Options, cancellationToken)
            ?? throw new InvalidDataException("De kaartinventaris is leeg.");
        Validate(snapshot);
        return snapshot;
    }

    public static void Validate(LogInventorySnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.InventoryTakenAtUtc == default || snapshot.Files is null || snapshot.Files.Count > 100_000)
            throw new InvalidDataException("De kaartinventaris mist een ondersteunde versie, inventarisdatum of geldige bestandenlijst.");
        _ = LogInventoryReconciler.Reconcile(snapshot.InventoryTakenAtUtc, snapshot.Files, []);
    }
}
