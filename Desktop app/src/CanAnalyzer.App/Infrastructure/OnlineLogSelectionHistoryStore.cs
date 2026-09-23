using System.IO;
using System.Text.Json;
using CanAnalyzer.App.Services;

namespace CanAnalyzer.App.Infrastructure;

public sealed class OnlineLogSelectionHistoryStore : IOnlineLogSelectionHistoryStore
{
    private const int MaximumEntries = 5;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public OnlineLogSelectionHistoryStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanAnalyzer");
        _path = Path.Combine(root, "online-selection-history.json");
    }

    public IReadOnlyList<OnlineLogSelectionHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return (JsonSerializer.Deserialize<List<OnlineLogSelectionHistoryEntry>>(File.ReadAllText(_path), SerializerOptions) ?? [])
                .Where(IsUsable)
                .OrderByDescending(entry => entry.SelectedAtUtc)
                .Take(MaximumEntries)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public async Task RememberAsync(OnlineLogSelectionHistoryEntry entry, CancellationToken cancellationToken)
    {
        if (!IsUsable(entry)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = Load().ToList();
            var fingerprint = Fingerprint(entry);
            entries.RemoveAll(candidate => string.Equals(Fingerprint(candidate), fingerprint, StringComparison.Ordinal));
            entries.Insert(0, entry);
            if (entries.Count > MaximumEntries) entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);

            var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("History path has no directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            var json = JsonSerializer.Serialize(entries, SerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsUsable(OnlineLogSelectionHistoryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.LoggerId) && entry.Files is { Count: > 0 };

    private static string Fingerprint(OnlineLogSelectionHistoryEntry entry) =>
        entry.LoggerId.Trim().ToUpperInvariant() + "|" +
        string.Join("|", entry.Files.Select(file => file.Key).OrderBy(key => key, StringComparer.Ordinal));
}
