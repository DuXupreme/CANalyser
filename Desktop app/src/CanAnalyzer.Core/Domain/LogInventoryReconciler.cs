namespace CanAnalyzer.Core.Domain;

public sealed record LogInventoryEntry(string LoggerId, string Session, string Name, long SizeBytes, string Sha256);
public sealed record ReceivedLogEvidence(string LoggerId, string Session, string Name, long SizeBytes,
    string? Sha256, DateTimeOffset? UploadedAtUtc);
public sealed record LogInventoryStatus(DateTimeOffset InventoryTakenAtUtc, int InventoriedFiles,
    long InventoriedBytes, int ConfirmedReceivedFiles, long ConfirmedReceivedBytes,
    IReadOnlyList<LogInventoryEntry> UnconfirmedFiles, DateTimeOffset? LastServerReceiptUtc)
{
    // A snapshot cannot describe files produced later on a disconnected logger.
    public long? CurrentQueueBytes => null;
    public bool InventoryFullyReceived => UnconfirmedFiles.Count == 0;
}

/// <summary>Matches a known SD snapshot by identity AND content; never guesses live backlog from time or free space.</summary>
public static class LogInventoryReconciler
{
    public static LogInventoryStatus Reconcile(DateTimeOffset inventoryTakenAtUtc,
        IEnumerable<LogInventoryEntry> inventory, IEnumerable<ReceivedLogEvidence> received)
    {
        var unique = new Dictionary<string, LogInventoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in inventory)
        {
            if (file is null || file.SizeBytes < 0 || !OnlineLogArchiveManifestCodec.IsSha256(file.Sha256))
                throw new InvalidDataException("De kaartinventaris mist een geldige grootte of SHA-256.");
            var key = Key(file.LoggerId, file.Session, file.Name);
            if (unique.TryGetValue(key, out var existing) &&
                (existing.SizeBytes != file.SizeBytes || !string.Equals(existing.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("De kaartinventaris bevat verschillende inhoud voor hetzelfde logdeel.");
            unique[key] = file;
        }
        var confirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? latest = null;
        var loggers = unique.Values.Select(file => file.LoggerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in received)
        {
            // Matching local content alone is not proof that a server received this file.
            if (file is null || !loggers.Contains(file.LoggerId) || file.UploadedAtUtc is not { } receipt || receipt == default) continue;
            if (latest is null || receipt > latest) latest = receipt;
            var key = Key(file.LoggerId, file.Session, file.Name);
            if (unique.TryGetValue(key, out var expected) && file.SizeBytes == expected.SizeBytes &&
                OnlineLogArchiveManifestCodec.IsSha256(file.Sha256) &&
                string.Equals(file.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)) confirmed.Add(key);
        }
        var remaining = unique.Where(pair => !confirmed.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        var total = unique.Values.Sum(file => file.SizeBytes);
        return new(inventoryTakenAtUtc.ToUniversalTime(), unique.Count, total, confirmed.Count,
            total - remaining.Sum(file => file.SizeBytes), remaining, latest);
    }

    private static string Key(string logger, string session, string name)
    {
        if (string.IsNullOrWhiteSpace(logger) || string.IsNullOrWhiteSpace(session) ||
            !OnlineLogSequencePolicy.TryParsePartNumber(name, out var part))
            throw new InvalidDataException("Logger, sessie of logdeel ontbreekt in de inventaris.");
        return $"{logger}/{session}/{part}";
    }
}
