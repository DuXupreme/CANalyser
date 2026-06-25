using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
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
    private IFrameSampleLookup? _sampleLookup;

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

    [ObservableProperty]
    private int _pageNumber = 1;

    public BusmasterViewModel()
    {
        ApplyFiltersCommand = new RelayCommand(() => ApplyFilters(resetPage: true));
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        PreviousPageCommand = new RelayCommand(PreviousPage, () => PageNumber > 1);
        NextPageCommand = new RelayCommand(NextPage, () => Messages.Count >= Math.Max(1, MaxRows));
    }

    public IRelayCommand ApplyFiltersCommand { get; }

    public IRelayCommand ResetFiltersCommand { get; }

    public IRelayCommand PreviousPageCommand { get; }

    public IRelayCommand NextPageCommand { get; }

    public void LoadDataset(CanDataset dataset)
    {
        _dataset = dataset;
        _sampleLookup = dataset.DecodedSamples as IFrameSampleLookup;

        ApplyFilters(resetPage: true);
    }

    partial void OnSelectedMessageChanged(BusmasterMessageRow? value)
    {
        SelectedSignals = value is null
            ? Array.Empty<BusmasterSignalRow>()
            : GetFrameSamples(value.FrameIndex)
                .OrderBy(sample => sample.SignalName, StringComparer.Ordinal)
                .Select(sample => new BusmasterSignalRow(sample))
                .ToList();
    }

    private void ApplyFilters(bool resetPage = false)
    {
        if (resetPage) PageNumber = 1;
        if (_dataset is null)
        {
            Messages = Array.Empty<BusmasterMessageRow>();
            SelectedMessage = null;
            MessageStatistics = "Geen dataset geladen.";
            return;
        }

        var search = SearchText?.Trim();
        var maxRows = Math.Clamp(MaxRows, 1, 2_000_000);
        var offset = checked((Math.Max(1, PageNumber) - 1) * maxRows);
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

        Messages = rows.Skip(offset).Take(maxRows).ToList();
        SelectedMessage = Messages.FirstOrDefault();
        MessageStatistics =
            $"Tonen: {Messages.Count:N0} / {_dataset.RawCount:N0} frames · " +
            $"DBC geïnterpreteerd: {Messages.Count(row => row.IsDecoded):N0}";
    }

    private BusmasterMessageRow CreateRow(RawCanFrame frame)
    {
        var decoded = TryGetFrameSummary(frame.FrameIndex, out var messageName);
        return new BusmasterMessageRow(frame, decoded, messageName);
    }

    private bool TryGetFrameSummary(long frameIndex, out string messageName)
    {
        if (_sampleLookup is not null)
            return _sampleLookup.TryGetFrameSummary(frameIndex, out messageName, out _);
        var sample = _dataset?.DecodedSamples.FirstOrDefault(item => item.FrameIndex == frameIndex);
        messageName = sample?.MessageName ?? string.Empty;
        return sample is not null;
    }

    private IReadOnlyList<DecodedSignalSample> GetFrameSamples(long frameIndex) =>
        _sampleLookup?.GetFrameSamples(frameIndex)
        ?? _dataset?.DecodedSamples.Where(sample => sample.FrameIndex == frameIndex).ToArray()
        ?? [];

    private void ResetFilters()
    {
        SearchText = null;
        ShowOnlyDecoded = false;
        MaxRows = 50_000;
        ApplyFilters(resetPage: true);
    }

    private void PreviousPage()
    {
        if (PageNumber <= 1) return;
        PageNumber--;
        ApplyFilters();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private void NextPage()
    {
        if (Messages.Count < Math.Max(1, MaxRows)) return;
        PageNumber++;
        ApplyFilters();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
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

}
