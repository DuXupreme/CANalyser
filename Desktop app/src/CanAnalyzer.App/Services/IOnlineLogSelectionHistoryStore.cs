namespace CanAnalyzer.App.Services;

public sealed record OnlineLogSelectionHistoryEntry(
    string LoggerId,
    string MachineName,
    DateTime FromDate,
    DateTime ToDate,
    DateTimeOffset SelectedAtUtc,
    IReadOnlyList<OnlineLogSelection> Files)
{
    // Older saved selections only contain LoggerId; those remain machine 1/2 selections.
    public string? MachineId { get; init; }
    public string TimeBasis { get; init; } = "recording";
    public string EffectiveMachineId => string.IsNullOrWhiteSpace(MachineId) ? LoggerId : MachineId;

    public string DisplayName
    {
        get
        {
            var sessions = Files.Select(file => file.Session).Distinct(StringComparer.Ordinal).Count();
            return $"{SelectedAtUtc.ToLocalTime():dd-MM HH:mm} · {MachineName} · {Files.Count:N0} bestand(en), {sessions:N0} sessie(s)";
        }
    }
}

public interface IOnlineLogSelectionHistoryStore
{
    IReadOnlyList<OnlineLogSelectionHistoryEntry> Load();

    Task RememberAsync(OnlineLogSelectionHistoryEntry entry, CancellationToken cancellationToken);
}
