using System.Collections.ObjectModel;
using System.IO;
using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class SettingsDiagnosticsViewModel
{
    private LogInventorySnapshot? _inventorySnapshot;
    private IReadOnlyList<ReceivedLogEvidence> _inventoryReceipts = [];
    [ObservableProperty] private string _inventoryStatus = "Open een historische SD-inventaris om ontvangen bestanden uit de huidige opname op inhoud te vergelijken.";
    public ObservableCollection<LogInventoryEntry> UnconfirmedInventoryFiles { get; } = [];

    [RelayCommand]
    private async Task OpenInventoryAsync()
    {
        var dialog = new OpenFileDialog { Title = "SD-inventaris openen", Filter = "SD-inventaris (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try { await LoadInventoryAsync(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { InventoryStatus = "Inventaris kon niet worden gelezen: " + ex.Message; }
    }

    public async Task LoadInventoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var snapshot = await LogInventorySnapshotCodec.ReadAsync(path, cancellationToken);
        _inventorySnapshot = snapshot;
        RefreshInventoryComparison();
    }

    private void UpdateInventorySources(CanDataset? dataset)
    {
        _inventoryReceipts = dataset?.SourceFiles
            .Where(source => source.UploadedAtUtc.HasValue && !string.IsNullOrWhiteSpace(source.ObjectKey) &&
                source.Logger is not null && source.Session is not null && source.SizeBytes.HasValue)
            .Select(source => new ReceivedLogEvidence(source.Logger!, source.Session!, source.Name,
                source.SizeBytes!.Value, source.Sha256, source.UploadedAtUtc)).ToArray() ?? [];
        RefreshInventoryComparison();
    }

    private void RefreshInventoryComparison()
    {
        UnconfirmedInventoryFiles.Clear();
        if (_inventorySnapshot is null) return;
        var result = LogInventoryReconciler.Reconcile(_inventorySnapshot.InventoryTakenAtUtc, _inventorySnapshot.Files, _inventoryReceipts);
        foreach (var file in result.UnconfirmedFiles) UnconfirmedInventoryFiles.Add(file);
        InventoryStatus = $"Inventaris van {result.InventoryTakenAtUtc.ToLocalTime():dd-MM-yyyy HH:mm:ss zzz}: " +
            $"{result.ConfirmedReceivedFiles:N0} van {result.InventoriedFiles:N0} bestanden aantoonbaar ontvangen " +
            $"({result.ConfirmedReceivedBytes:N0} van {result.InventoriedBytes:N0} bytes).\n" +
            (result.LastServerReceiptUtc is { } receipt ? $"Laatste serverontvangst in de geladen brongegevens: {receipt.ToLocalTime():dd-MM-yyyy HH:mm:ss zzz}.\n" : "Geen serverontvangst in de geladen brongegevens.\n") +
            "Alleen geladen brongegevens worden vergeleken. Niet bevestigd betekent niet bewezen ontbrekend. De actuele uploadwachtrij blijft onbekend, ook als deze inventaris volledig ontvangen is.";
    }
}
