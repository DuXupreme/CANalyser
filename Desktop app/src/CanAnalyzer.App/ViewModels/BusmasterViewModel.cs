using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanAnalyzer.App.ViewModels;

/// <summary>
/// BUSMASTER-inspired message window with selection-based DBC interpretation.
/// </summary>
public sealed partial class BusmasterViewModel : ObservableObject
{
    private CanDataset? _dataset;
    private IReadOnlyDictionary<FrameSampleKey, IReadOnlyList<DecodedSignalSample>> _signalsByFrame =
        new Dictionary<FrameSampleKey, IReadOnlyList<DecodedSignalSample>>();

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private bool _showOnlyDecoded;

    [ObservableProperty]
    private int _maxRows = 50_000;

    [ObservableProperty]
    private string _messageStatistics = "Geen dataset geladen.";

    [ObservableProperty]
    private IReadOnlyList<BusmasterMessageRow> _messages = Array.Empty<BusmasterMessageRow>();

    [ObservableProperty]
    private BusmasterMessageRow? _selectedMessage;

    [ObservableProperty]
    private IReadOnlyList<BusmasterSignalRow> _selectedSignals = Array.Empty<BusmasterSignalRow>();

    public BusmasterViewModel()
    {
        ApplyFiltersCommand = new RelayCommand(ApplyFilters);
        ResetFiltersCommand = new RelayCommand(ResetFilters);
    }

    public IRelayCommand ApplyFiltersCommand { get; }

    public IRelayCommand ResetFiltersCommand { get; }

    public void LoadDataset(CanDataset dataset)
    {
        _dataset = dataset;
        _signalsByFrame = dataset.DecodedSamples
            .GroupBy(sample => new FrameSampleKey(sample.FrameId, BitConverter.SingleToInt32Bits(sample.TimeSeconds)))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DecodedSignalSample>)group
                    .GroupBy(sample => (sample.MessageName, sample.SignalName))
                    .Select(signalGroup => signalGroup.First())
                    .OrderBy(sample => sample.SignalName, StringComparer.Ordinal)
                    .ToList());

        ApplyFilters();
    }

    partial void OnSelectedMessageChanged(BusmasterMessageRow? value)
    {
        SelectedSignals = value is null
            ? Array.Empty<BusmasterSignalRow>()
            : value.DecodedSignals
                .OrderBy(sample => sample.SignalName, StringComparer.Ordinal)
                .Select(sample => new BusmasterSignalRow(sample))
                .ToList();
    }

    private void ApplyFilters()
    {
        if (_dataset is null)
        {
            Messages = Array.Empty<BusmasterMessageRow>();
            SelectedMessage = null;
            MessageStatistics = "Geen dataset geladen.";
            return;
        }

        var search = SearchText?.Trim();
        var maxRows = Math.Clamp(MaxRows, 1, 2_000_000);
        var rows = _dataset.RawFrames
            .Select(CreateRow)
            .Where(row => !ShowOnlyDecoded || row.IsDecoded);

        if (!string.IsNullOrWhiteSpace(search))
        {
            if (TryParseId(search, out var frameId))
            {
                rows = rows.Where(row => row.Source.Id == frameId);
            }
            else
            {
                rows = rows.Where(row =>
                    row.IdHex.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    row.MessageName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    row.DataBytes.Contains(search, StringComparison.OrdinalIgnoreCase));
            }
        }

        Messages = rows.Take(maxRows).ToList();
        SelectedMessage = Messages.FirstOrDefault();
        MessageStatistics =
            $"Tonen: {Messages.Count:N0} / {_dataset.RawCount:N0} frames · " +
            $"DBC geïnterpreteerd: {Messages.Count(row => row.IsDecoded):N0}";
    }

    private BusmasterMessageRow CreateRow(RawCanFrame frame)
    {
        var normalizedId = CanIdUtilities.NormalizeDbcFrameId(
            frame.Id,
            frame.IsExtended || frame.Id > 0x7FF);
        var key = new FrameSampleKey(
            normalizedId,
            BitConverter.SingleToInt32Bits((float)frame.TimeSeconds));

        return new BusmasterMessageRow(
            frame,
            _signalsByFrame.TryGetValue(key, out var samples)
                ? samples
                : Array.Empty<DecodedSignalSample>());
    }

    private void ResetFilters()
    {
        SearchText = null;
        ShowOnlyDecoded = false;
        MaxRows = 50_000;
        ApplyFilters();
    }

    private static bool TryParseId(string token, out uint frameId)
    {
        try
        {
            frameId = HexUtilities.ParseIntAuto(token);
            return true;
        }
        catch
        {
            frameId = 0;
            return false;
        }
    }

    private readonly record struct FrameSampleKey(uint FrameId, int TimeBits);
}
