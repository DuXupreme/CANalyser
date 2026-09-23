using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;

namespace CanAnalyzer.App.ViewModels;

/// <summary>
/// BUSMASTER-inspired message window with selection-based DBC interpretation.
/// </summary>
public sealed partial class BusmasterViewModel : ObservableObject
{
    private CanDataset? _dataset;
    private IFrameSampleLookup? _sampleLookup;
    private readonly ITelemetryService? _telemetryService;

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
    private string? _signalWatchSearchText;

    [ObservableProperty]
    private IReadOnlyList<BusmasterSignalWatchRow> _signalWatchRows = Array.Empty<BusmasterSignalWatchRow>();

    [ObservableProperty]
    private string _signalWatchStatistics = "Geen dataset geladen.";

    [ObservableProperty]
    private int _pageNumber = 1;

    private IReadOnlyList<BusmasterSignalWatchRow> _allSignalWatchRows = Array.Empty<BusmasterSignalWatchRow>();

    public BusmasterViewModel(ITelemetryService? telemetryService = null)
    {
        _telemetryService = telemetryService;
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
        BuildSignalWatchRows(dataset);
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

    partial void OnSignalWatchSearchTextChanged(string? value)
    {
        ApplySignalWatchFilter();
    }

    private void ApplyFilters(bool resetPage = false)
    {
        var timer = Stopwatch.StartNew();
        var search = SearchText?.Trim();
        var operationId = _telemetryService?.BeginCriticalOperation("busmaster_filter", new Dictionary<string, object?>
        {
            ["search_kind"] = TryParseId(search ?? string.Empty, out _) ? "can_id" : "text_or_empty"
        });
        _ = _telemetryService?.TrackEventAsync("busmaster_filter_started", new Dictionary<string, object?>
        {
            ["has_search"] = !string.IsNullOrWhiteSpace(search),
            ["search_kind"] = TryParseId(search ?? string.Empty, out _) ? "can_id" : string.IsNullOrWhiteSpace(search) ? "none" : "text",
            ["show_only_decoded"] = ShowOnlyDecoded,
            ["max_rows_bucket"] = TelemetryBuckets.Count(MaxRows)
        });
        try
        {
            ApplyFiltersCore(resetPage, search);
            timer.Stop();
            _ = _telemetryService?.TrackEventAsync("busmaster_filter_completed", new Dictionary<string, object?>
            {
                ["duration_ms"] = (long)timer.Elapsed.TotalMilliseconds,
                ["result_rows_bucket"] = TelemetryBuckets.Count(Messages.Count),
                ["page"] = PageNumber
            });
        }
        catch (Exception ex)
        {
            timer.Stop();
            _ = _telemetryService?.TrackEventAsync("busmaster_filter_failed", new Dictionary<string, object?>
            {
                ["duration_ms"] = (long)timer.Elapsed.TotalMilliseconds,
                ["exception_type"] = ex.GetType().Name,
                ["source"] = "apply_filters"
            });
            throw;
        }
        finally
        {
            if (operationId is not null) _telemetryService?.CompleteCriticalOperation(operationId);
        }
    }

    private void ApplyFiltersCore(bool resetPage, string? search)
    {
        if (resetPage) PageNumber = 1;
        if (_dataset is null)
        {
            Messages = Array.Empty<BusmasterMessageRow>();
            SelectedMessage = null;
            MessageStatistics = "Geen dataset geladen.";
            _allSignalWatchRows = Array.Empty<BusmasterSignalWatchRow>();
            SignalWatchRows = Array.Empty<BusmasterSignalWatchRow>();
            SignalWatchStatistics = "Geen dataset geladen.";
            return;
        }

        var maxRows = Math.Clamp(MaxRows, 1, 2_000_000);
        var offset = checked((Math.Max(1, PageNumber) - 1) * maxRows);
        // Reject other IDs before resolving decoded metadata for millions of unrelated frames.
        IEnumerable<RawCanFrame> source = _dataset.RawFrames;
        if (!string.IsNullOrWhiteSpace(search) && TryParseId(search, out var selectedId))
            source = source.Where(frame => frame.Id == selectedId);
        var rows = source
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
        SignalWatchSearchText = null;
        ApplyFilters(resetPage: true);
    }

    private void BuildSignalWatchRows(CanDataset dataset)
    {
        if (dataset.DecodedSamples.Count == 0)
        {
            _allSignalWatchRows = Array.Empty<BusmasterSignalWatchRow>();
            SignalWatchRows = Array.Empty<BusmasterSignalWatchRow>();
            SignalWatchStatistics = "Geen gedecodeerde signalen beschikbaar.";
            return;
        }

        if (dataset.DecodedSamples is ISignalSampleLookup lookup)
        {
            _allSignalWatchRows = lookup.GetSignalSummaries()
                .Select(summary => new BusmasterSignalWatchRow(summary.Latest.Identity,
                    summary.Latest.TimestampNanoseconds, summary.Latest.FrameIndex, summary.Latest.Value,
                    summary.Latest.RawValueHex, summary.Latest.Unit, summary.Count, summary.Minimum, summary.Maximum))
                .OrderBy(row => row.MessageName).ThenBy(row => row.Name).ThenBy(row => row.Channel).ThenBy(row => row.FrameIdHex)
                .ToList();
            ApplySignalWatchFilter();
            return;
        }

        var stats = new Dictionary<SignalIdentity, SignalWatchAccumulator>();
        foreach (var sample in dataset.DecodedSamples)
        {
            if (!stats.TryGetValue(sample.Identity, out var accumulator))
            {
                stats[sample.Identity] = new SignalWatchAccumulator(sample);
                continue;
            }

            accumulator.Update(sample);
        }

        _allSignalWatchRows = stats.Values
            .Select(static item => item.ToRow())
            .OrderBy(static row => row.MessageName, StringComparer.Ordinal)
            .ThenBy(static row => row.Name, StringComparer.Ordinal)
            .ThenBy(static row => row.Channel, StringComparer.Ordinal)
            .ThenBy(static row => row.FrameIdHex, StringComparer.Ordinal)
            .ToList();

        ApplySignalWatchFilter();
    }

    private void ApplySignalWatchFilter()
    {
        var search = SignalWatchSearchText?.Trim();
        var rows = _allSignalWatchRows;
        if (!string.IsNullOrWhiteSpace(search))
        {
            rows = rows
                .Where(row =>
                    row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    row.MessageName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    row.FrameIdHex.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    row.Channel.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    row.Unit.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        SignalWatchRows = rows;
        SignalWatchStatistics = _dataset is null
            ? "Geen dataset geladen."
            : $"Signal Watch: {SignalWatchRows.Count:N0} / {_allSignalWatchRows.Count:N0} signalen, {_dataset.DecodedSamples.Count:N0} gedecodeerde meetpunten.";
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

    private sealed class SignalWatchAccumulator
    {
        private readonly SignalIdentity _identity;
        private long _lastTimestampNanoseconds;
        private long _lastFrameIndex;
        private double _lastValue;
        private string _rawValueHex;
        private string _unit;
        private int _count = 1;
        private double _minimum;
        private double _maximum;

        public SignalWatchAccumulator(DecodedSignalSample sample)
        {
            _identity = sample.Identity;
            _lastTimestampNanoseconds = sample.TimestampNanoseconds;
            _lastFrameIndex = sample.FrameIndex;
            _lastValue = sample.Value;
            _rawValueHex = sample.RawValueHex;
            _unit = sample.Unit;
            _minimum = sample.Value;
            _maximum = sample.Value;
        }

        public void Update(DecodedSignalSample sample)
        {
            _count++;
            if (sample.Value < _minimum)
            {
                _minimum = sample.Value;
            }

            if (sample.Value > _maximum)
            {
                _maximum = sample.Value;
            }

            if (sample.TimestampNanoseconds < _lastTimestampNanoseconds ||
                (sample.TimestampNanoseconds == _lastTimestampNanoseconds && sample.FrameIndex < _lastFrameIndex))
            {
                return;
            }

            _lastTimestampNanoseconds = sample.TimestampNanoseconds;
            _lastFrameIndex = sample.FrameIndex;
            _lastValue = sample.Value;
            _rawValueHex = sample.RawValueHex;
            _unit = sample.Unit;
        }

        public BusmasterSignalWatchRow ToRow() =>
            new(
                _identity,
                _lastTimestampNanoseconds,
                _lastFrameIndex,
                _lastValue,
                _rawValueHex,
                _unit,
                _count,
                _minimum,
                _maximum);
    }
}
