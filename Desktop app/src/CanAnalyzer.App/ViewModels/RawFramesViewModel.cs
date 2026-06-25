using System.Globalization;
using System.Text;
using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CanAnalyzer.App.ViewModels;

/// <summary>
/// PCAN/raw frame viewer tab.
/// </summary>
public sealed partial class RawFramesViewModel : ObservableObject
{
    private readonly IRawFrameFilterService _filterService;
    private CanDataset? _dataset;
    private IFrameSampleLookup? _sampleLookup;

    [ObservableProperty]
    private string? _idFilter;

    [ObservableProperty]
    private string? _dataContainsHex;

    [ObservableProperty]
    private string? _typeContains;

    [ObservableProperty]
    private string? _channelContains;

    [ObservableProperty]
    private double? _timeStart;

    [ObservableProperty]
    private double? _timeEnd;

    [ObservableProperty]
    private int _maxRows = 50_000;

    [ObservableProperty]
    private string _extendedFilterMode = "All";

    [ObservableProperty]
    private string _frameStatistics = "Geen dataset geladen.";

    [ObservableProperty]
    private IReadOnlyList<RawFrameRow> _filteredFrames = Array.Empty<RawFrameRow>();

    [ObservableProperty]
    private int _pageNumber = 1;

    public RawFramesViewModel(IRawFrameFilterService filterService)
    {
        _filterService = filterService;
        ApplyFiltersCommand = new RelayCommand(() => ApplyFilters(resetPage: true));
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        PreviousPageCommand = new RelayCommand(PreviousPage, () => PageNumber > 1);
        NextPageCommand = new RelayCommand(NextPage, () => FilteredFrames.Count >= Math.Max(1, MaxRows));
    }

    public IReadOnlyList<string> ExtendedFilterModes { get; } = ["All", "Extended only", "Standard only"];

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

    public void ApplyFilterOptions(RawFrameFilterOptions options)
    {
        IdFilter = options.IdFilter;
        DataContainsHex = options.DataContainsHex;
        TypeContains = options.TypeContains;
        ChannelContains = options.ChannelContains;
        TimeStart = options.TimeStart;
        TimeEnd = options.TimeEnd;
        MaxRows = options.MaxRows <= 0 ? 50_000 : options.MaxRows;
        ExtendedFilterMode = options.IsExtended switch
        {
            true => "Extended only",
            false => "Standard only",
            null => "All"
        };
    }

    public RawFrameFilterOptions CaptureFilterOptions()
    {
        return new RawFrameFilterOptions
        {
            IdFilter = IdFilter,
            DataContainsHex = DataContainsHex,
            TypeContains = TypeContains,
            ChannelContains = ChannelContains,
            TimeStart = TimeStart,
            TimeEnd = TimeEnd,
            MaxRows = Math.Clamp(MaxRows, 1, 2_000_000),
            IsExtended = ExtendedFilterMode switch
            {
                "Extended only" => true,
                "Standard only" => false,
                _ => null
            },
            Offset = checked((Math.Max(1, PageNumber) - 1) * Math.Clamp(MaxRows, 1, 2_000_000))
        };
    }

    public string BuildFrameDetailsText(RawFrameRow row)
    {
        var frame = row.Source;
        var builder = new StringBuilder()
            .AppendLine($"Tijd [s]: {frame.TimeSeconds:G17}")
            .AppendLine($"Tijd [ns]: {frame.TimestampNanoseconds}")
            .AppendLine($"Frame-index: {frame.FrameIndex}")
            .AppendLine($"Bronregel: {frame.SourceLineNumber}")
            .AppendLine($"Type: {frame.Type}")
            .AppendLine($"Kanaal: {frame.Channel}")
            .AppendLine($"ID: {frame.IdHex} ({frame.Id})")
            .AppendLine($"DLC: {frame.Dlc}")
            .AppendLine($"Data: {frame.DataHex}")
            .AppendLine($"ASCII: {frame.DataAscii}")
            .AppendLine()
            .AppendLine("Decoded signals (DBC, dec):");

        if (_dataset is null || _dataset.DecodedSamples.Count == 0)
        {
            builder.AppendLine("- Geen gedecodeerde signalen beschikbaar.");
            return builder.ToString();
        }

        var decodedForFrame = GetDecodedSamplesForRawFrame(frame);
        if (decodedForFrame.Count == 0)
        {
            builder.AppendLine("- Geen DBC-signalen gevonden voor dit frame/tijdstip.");
            return builder.ToString();
        }

        foreach (var messageGroup in decodedForFrame
                     .GroupBy(sample => sample.MessageName, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"- Message: {messageGroup.Key}");
            foreach (var sample in messageGroup.OrderBy(item => item.SignalName, StringComparer.Ordinal))
            {
                builder.AppendLine($"  {sample.SignalName} = {sample.Value.ToString("G17", CultureInfo.InvariantCulture)} ({sample.RawValueHex})");
            }
        }

        return builder.ToString();
    }

    private List<DecodedSignalSample> GetDecodedSamplesForRawFrame(RawCanFrame frame)
    {
        if (_dataset is null || _dataset.DecodedSamples.Count == 0)
        {
            return [];
        }

        return _sampleLookup?.GetFrameSamples(frame.FrameIndex).ToList()
               ?? _dataset.DecodedSamples.Where(sample => sample.FrameIndex == frame.FrameIndex).ToList();
    }

    private void ApplyFilters(bool resetPage = false)
    {
        if (resetPage) PageNumber = 1;
        if (_dataset is null)
        {
            FilteredFrames = Array.Empty<RawFrameRow>();
            FrameStatistics = "Geen dataset geladen.";
            return;
        }

        var options = CaptureFilterOptions();
        var rows = _filterService.Apply(_dataset.RawFrames, options);

        FilteredFrames = rows.Select(frame => new RawFrameRow(frame)).ToList();

        FrameStatistics = $"Pagina {PageNumber:N0}: weergegeven {FilteredFrames.Count:N0} frames vanaf gefilterde offset {options.Offset:N0}; dataset totaal {_dataset.RawCount:N0}. De dataset zelf is niet afgekapt.";
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private void ResetFilters()
    {
        IdFilter = null;
        DataContainsHex = null;
        TypeContains = null;
        ChannelContains = null;
        TimeStart = null;
        TimeEnd = null;
        MaxRows = 50_000;
        ExtendedFilterMode = "All";
        ApplyFilters(resetPage: true);
    }

    private void PreviousPage()
    {
        if (PageNumber <= 1) return;
        PageNumber--;
        ApplyFilters();
    }

    private void NextPage()
    {
        if (FilteredFrames.Count < Math.Max(1, MaxRows)) return;
        PageNumber++;
        ApplyFilters();
    }
}
