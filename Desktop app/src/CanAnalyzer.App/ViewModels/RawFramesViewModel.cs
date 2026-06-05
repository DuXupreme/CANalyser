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

    public RawFramesViewModel(IRawFrameFilterService filterService)
    {
        _filterService = filterService;
        ApplyFiltersCommand = new RelayCommand(ApplyFilters);
        ResetFiltersCommand = new RelayCommand(ResetFilters);
    }

    public IReadOnlyList<string> ExtendedFilterModes { get; } = ["All", "Extended only", "Standard only"];

    public IRelayCommand ApplyFiltersCommand { get; }

    public IRelayCommand ResetFiltersCommand { get; }

    public void LoadDataset(CanDataset dataset)
    {
        _dataset = dataset;
        ApplyFilters();
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
            }
        };
    }

    public string BuildFrameDetailsText(RawFrameRow row)
    {
        var frame = row.Source;
        var builder = new StringBuilder()
            .AppendLine($"Tijd [s]: {frame.TimeSeconds:F6}")
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
                builder.AppendLine($"  {sample.SignalName} = {sample.Value.ToString("0.######", CultureInfo.InvariantCulture)}");
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

        var normalizedId = CanIdUtilities.NormalizeDbcFrameId(frame.Id, frame.IsExtended || frame.Id > 0x7FF);
        var frameTime = (float)frame.TimeSeconds;
        const float exactTolerance = 0.0005f;

        var exactMatches = _dataset.DecodedSamples
            .Where(sample =>
                sample.FrameId == normalizedId &&
                Math.Abs(sample.TimeSeconds - frameTime) <= exactTolerance)
            .ToList();
        if (exactMatches.Count > 0)
        {
            return exactMatches;
        }

        var candidates = _dataset.DecodedSamples
            .Where(sample => sample.FrameId == normalizedId)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var nearestTime = candidates
            .OrderBy(sample => Math.Abs(sample.TimeSeconds - frameTime))
            .Select(sample => sample.TimeSeconds)
            .First();

        const float nearestTolerance = 0.002f;
        return candidates
            .Where(sample => Math.Abs(sample.TimeSeconds - nearestTime) <= nearestTolerance)
            .ToList();
    }

    private void ApplyFilters()
    {
        if (_dataset is null)
        {
            FilteredFrames = Array.Empty<RawFrameRow>();
            FrameStatistics = "Geen dataset geladen.";
            return;
        }

        var options = CaptureFilterOptions();
        var rows = _filterService.Apply(_dataset.RawFrames, options);

        FilteredFrames = rows.Select(frame => new RawFrameRow(frame)).ToList();

        FrameStatistics = $"Tonen: {FilteredFrames.Count:N0} / {_dataset.RawCount:N0} frames";
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
        ApplyFilters();
    }
}
