using System.Collections.ObjectModel;
using System.ComponentModel;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Globalization;
using System.Windows.Data;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Annotations;

namespace CanAnalyzer.App.ViewModels;

/// <summary>
/// Analysis tab view model: signal selection, plot groups, plotting, summaries, presets.
/// </summary>
public sealed partial class AnalysisViewModel : ObservableObject
{
    private readonly IPlotModelBuilder _plotModelBuilder;
    private readonly IFileDialogService _fileDialogService;
    private readonly IPlotWindowService _plotWindowService;
    private readonly IPresetSerializer _presetSerializer;
    private readonly IXAxisSyncService _xAxisSyncService;
    private readonly ILogger<AnalysisViewModel> _logger;
    private CanDataset? _dataset;
    private PlotPanelModel? _activeCursorPanel;
    private bool _suppressAutoRebuild;

    [ObservableProperty]
    private PlotGroupViewModel? _selectedPlotGroup;

    [ObservableProperty]
    private string? _frameIdFilter;

    [ObservableProperty]
    private double? _timeStart;

    [ObservableProperty]
    private double? _timeEnd;

    [ObservableProperty]
    private int _maxPointsPerTrace = 4000;

    [ObservableProperty]
    private bool _useDownsampling = true;

    [ObservableProperty]
    private int _subplotHeight = 280;

    [ObservableProperty]
    private int _signalListHeight = 420;

    [ObservableProperty]
    private string _signalSearchText = string.Empty;

    [ObservableProperty]
    private bool _normalizeSignals;

    [ObservableProperty]
    private bool _stepPlot;

    [ObservableProperty]
    private bool _markersOnly;

    [ObservableProperty]
    private bool _showLegend = true;

    [ObservableProperty]
    private bool _linkXAxisAcrossPanels = true;

    [ObservableProperty]
    private bool _linkYAxisAcrossPanels = true;

    [ObservableProperty]
    private string _presetStatus = string.Empty;

    [ObservableProperty]
    private string _decodeDiagnostics = string.Empty;

    [ObservableProperty]
    private bool _autoOpenDetachedOnApply;

    [ObservableProperty]
    private double? _cursorTime;

    [ObservableProperty]
    private double? _flagATime;

    [ObservableProperty]
    private double? _flagBTime;

    [ObservableProperty]
    private string _cursorInfo = "Cursor: - | A: - | B: - | dt: -";

    [ObservableProperty]
    private string _cursorValueInfo = "Signaalwaarde: -";

    public AnalysisViewModel(
        IPlotModelBuilder plotModelBuilder,
        IFileDialogService fileDialogService,
        IPlotWindowService plotWindowService,
        IPresetSerializer presetSerializer,
        IXAxisSyncService xAxisSyncService,
        ILogger<AnalysisViewModel> logger)
    {
        _plotModelBuilder = plotModelBuilder;
        _fileDialogService = fileDialogService;
        _plotWindowService = plotWindowService;
        _presetSerializer = presetSerializer;
        _xAxisSyncService = xAxisSyncService;
        _logger = logger;

        BuildGroupsFromSelectionCommand = new RelayCommand(BuildGroupsFromSelection);
        CreateSingleGroupFromSelectionCommand = new RelayCommand(CreateSingleGroupFromSelection);
        SelectAllVisibleSignalsCommand = new RelayCommand(() => SetSelectionForVisibleSignals(true));
        SelectNoVisibleSignalsCommand = new RelayCommand(() => SetSelectionForVisibleSignals(false));
        ClearSignalSearchCommand = new RelayCommand(ClearSignalSearch);
        AddGroupCommand = new RelayCommand(AddGroup);
        RemoveSelectedGroupCommand = new RelayCommand(RemoveSelectedGroup);
        MoveGroupUpCommand = new RelayCommand(() => MoveGroup(-1));
        MoveGroupDownCommand = new RelayCommand(() => MoveGroup(1));
        AddSelectedSignalsToGroupCommand = new RelayCommand(AddSelectedSignalsToGroup);
        RemoveSignalFromGroupCommand = new RelayCommand<GroupSignalItem?>(RemoveSignalFromGroup);
        ApplyGroupsCommand = new RelayCommand(ApplyGroupsFromUi);
        OpenPlotsInWindowCommand = new RelayCommand(OpenPlotsInWindow);
        SetFlagAFromCursorCommand = new RelayCommand(SetFlagAFromCursor);
        SetFlagBFromCursorCommand = new RelayCommand(SetFlagBFromCursor);
        JumpFlagBToNextChangeCommand = new RelayCommand(JumpFlagBToNextChange);
        ClearFlagsCommand = new RelayCommand(ClearFlags);
        ResetAllPlotsCommand = new RelayCommand(ResetAllPlots);
        ExportPresetCommand = new AsyncRelayCommand(ExportPresetAsync);
        ImportPresetCommand = new AsyncRelayCommand(ImportPresetAsync);

        FilteredSignalsView = CollectionViewSource.GetDefaultView(AvailableSignals);
        FilteredSignalsView.Filter = item => item is SignalSelectionItem signal && MatchesSignalSearch(signal);
    }

    public ObservableCollection<SignalSelectionItem> AvailableSignals { get; } = [];

    public ICollectionView FilteredSignalsView { get; }

    public ObservableCollection<PlotGroupViewModel> PlotGroups { get; } = [];

    public ObservableCollection<PlotPanelModel> PlotPanels { get; } = [];

    public ObservableCollection<MessageSummary> MessageSummaries { get; } = [];

    public IRelayCommand BuildGroupsFromSelectionCommand { get; }

    public IRelayCommand CreateSingleGroupFromSelectionCommand { get; }

    public IRelayCommand SelectAllVisibleSignalsCommand { get; }

    public IRelayCommand SelectNoVisibleSignalsCommand { get; }

    public IRelayCommand ClearSignalSearchCommand { get; }

    public IRelayCommand AddGroupCommand { get; }

    public IRelayCommand RemoveSelectedGroupCommand { get; }

    public IRelayCommand MoveGroupUpCommand { get; }

    public IRelayCommand MoveGroupDownCommand { get; }

    public IRelayCommand AddSelectedSignalsToGroupCommand { get; }

    public IRelayCommand<GroupSignalItem?> RemoveSignalFromGroupCommand { get; }

    public IRelayCommand ApplyGroupsCommand { get; }

    public IRelayCommand OpenPlotsInWindowCommand { get; }

    public IRelayCommand SetFlagAFromCursorCommand { get; }

    public IRelayCommand SetFlagBFromCursorCommand { get; }

    public IRelayCommand JumpFlagBToNextChangeCommand { get; }

    public IRelayCommand ClearFlagsCommand { get; }

    public IRelayCommand ResetAllPlotsCommand { get; }

    public IAsyncRelayCommand ExportPresetCommand { get; }

    public IAsyncRelayCommand ImportPresetCommand { get; }

    partial void OnNormalizeSignalsChanged(bool value) => TriggerLiveRebuild();

    partial void OnStepPlotChanged(bool value) => TriggerLiveRebuild();

    partial void OnMarkersOnlyChanged(bool value) => TriggerLiveRebuild();

    partial void OnShowLegendChanged(bool value) => TriggerLiveRebuild();

    partial void OnLinkXAxisAcrossPanelsChanged(bool value)
    {
        ApplyAxisSyncConfiguration();
    }

    partial void OnLinkYAxisAcrossPanelsChanged(bool value)
    {
        ApplyAxisSyncConfiguration();
    }

    partial void OnMaxPointsPerTraceChanged(int value) => TriggerLiveRebuild();

    partial void OnUseDownsamplingChanged(bool value) => TriggerLiveRebuild();

    partial void OnSignalSearchTextChanged(string value)
    {
        FilteredSignalsView.Refresh();
    }

    public void LoadDataset(CanDataset dataset)
    {
        _dataset = dataset;
        AvailableSignals.Clear();
        foreach (var label in dataset.SignalLabels)
        {
            AvailableSignals.Add(new SignalSelectionItem(label));
        }

        FilteredSignalsView.Refresh();

        MessageSummaries.Clear();
        foreach (var summary in dataset.MessageSummaries)
        {
            MessageSummaries.Add(summary);
        }

        DecodeDiagnostics = dataset.Diagnostics.DecodeNote;

        PlotGroups.Clear();
        PlotPanels.Clear();
        SelectedPlotGroup = null;
        CursorTime = null;
        FlagATime = null;
        FlagBTime = null;
        CursorInfo = "Cursor: - | A: - | B: - | dt: -";
        CursorValueInfo = "Signaalwaarde: -";
        _activeCursorPanel = null;
        _xAxisSyncService.Bind([]);
        PresetStatus = "Dataset geladen. Kies signalen en maak plotgroepen om grafieken te tonen.";
    }

    public void ApplyViewOptions(PlotViewOptions options)
    {
        _suppressAutoRebuild = true;
        try
        {
            FrameIdFilter = options.FrameIdFilter;
            TimeStart = options.TimeStart;
            TimeEnd = options.TimeEnd;
            MaxPointsPerTrace = options.MaxPointsPerTrace;
            SubplotHeight = options.SubplotHeight;
            SignalListHeight = options.SignalListHeight;
            var legacyOptions = options.PlotOptions ?? [];
            UseDownsampling = options.UseDownsampling && !legacyOptions.Contains("disable_downsampling");
            NormalizeSignals = options.NormalizeSignals || legacyOptions.Contains("normalize");
            StepPlot = options.StepPlot || legacyOptions.Contains("step");
            MarkersOnly = options.MarkersOnly || legacyOptions.Contains("markers");
            ShowLegend = options.ShowLegend;
            LinkXAxisAcrossPanels = options.LinkXAxisAcrossPanels && !legacyOptions.Contains("unlink_x_sync");
            LinkYAxisAcrossPanels = !legacyOptions.Contains("unlink_y_sync");
            AutoOpenDetachedOnApply = options.AutoOpenDetachedOnApply;
        }
        finally
        {
            _suppressAutoRebuild = false;
        }
    }

    public PlotViewOptions CaptureViewOptions()
    {
        var list = new List<string>();
        if (NormalizeSignals)
        {
            list.Add("normalize");
        }

        if (StepPlot)
        {
            list.Add("step");
        }

        if (MarkersOnly)
        {
            list.Add("markers");
        }

        if (!LinkYAxisAcrossPanels)
        {
            list.Add("unlink_y_sync");
        }

        if (!LinkXAxisAcrossPanels)
        {
            list.Add("unlink_x_sync");
        }

        if (!UseDownsampling)
        {
            list.Add("disable_downsampling");
        }

        return new PlotViewOptions
        {
            FrameIdFilter = FrameIdFilter,
            TimeStart = TimeStart,
            TimeEnd = TimeEnd,
            MaxPointsPerTrace = Math.Clamp(MaxPointsPerTrace, 200, 200_000),
            UseDownsampling = UseDownsampling,
            SubplotHeight = Math.Clamp(SubplotHeight, 160, 1200),
            SignalListHeight = Math.Clamp(SignalListHeight, 180, 1200),
            NormalizeSignals = NormalizeSignals,
            StepPlot = StepPlot,
            MarkersOnly = MarkersOnly,
            ShowLegend = ShowLegend,
            LinkXAxisAcrossPanels = LinkXAxisAcrossPanels,
            AutoOpenDetachedOnApply = AutoOpenDetachedOnApply,
            PlotOptions = list
        };
    }

    public IReadOnlyList<PlotGroup> CapturePlotGroups()
    {
        return PlotGroups.Select(group => group.ToDomainModel()).ToList();
    }

    public void LoadPlotGroups(IReadOnlyList<PlotGroup> plotGroups)
    {
        PlotGroups.Clear();
        foreach (var group in plotGroups)
        {
            PlotGroups.Add(PlotGroupViewModel.FromDomainModel(group));
        }

        SelectedPlotGroup = PlotGroups.FirstOrDefault();
    }

    private void BuildGroupsFromSelection()
    {
        var selected = GetSelectedSignalLabels();
        if (selected.Count == 0)
        {
            return;
        }

        PlotGroups.Clear();
        foreach (var label in selected)
        {
            var group = new PlotGroupViewModel { Title = label };
            group.AddSignals([label]);
            PlotGroups.Add(group);
        }

        SelectedPlotGroup = PlotGroups.FirstOrDefault();
        RebuildPlots();
    }

    private void CreateSingleGroupFromSelection()
    {
        var selected = GetSelectedSignalLabels();
        if (selected.Count == 0)
        {
            return;
        }

        var group = new PlotGroupViewModel
        {
            Title = selected.Count == 1
                ? selected[0]
                : $"Groep {PlotGroups.Count + 1}"
        };
        group.AddSignals(selected);

        PlotGroups.Add(group);
        SelectedPlotGroup = group;
        RebuildPlots();
    }

    private void AddGroup()
    {
        var group = new PlotGroupViewModel();
        PlotGroups.Add(group);
        SelectedPlotGroup = group;
    }

    private void RemoveSelectedGroup()
    {
        if (SelectedPlotGroup is null)
        {
            return;
        }

        var idx = PlotGroups.IndexOf(SelectedPlotGroup);
        PlotGroups.Remove(SelectedPlotGroup);
        SelectedPlotGroup = PlotGroups.ElementAtOrDefault(Math.Max(0, idx - 1));
        RebuildPlots();
    }

    private void MoveGroup(int delta)
    {
        if (SelectedPlotGroup is null)
        {
            return;
        }

        var current = PlotGroups.IndexOf(SelectedPlotGroup);
        if (current < 0)
        {
            return;
        }

        var target = current + delta;
        if (target < 0 || target >= PlotGroups.Count)
        {
            return;
        }

        PlotGroups.Move(current, target);
        SelectedPlotGroup = PlotGroups[target];
        RebuildPlots();
    }

    private void AddSelectedSignalsToGroup()
    {
        if (SelectedPlotGroup is null)
        {
            return;
        }

        var selected = GetSelectedSignalLabels();
        SelectedPlotGroup.AddSignals(selected);
    }

    private List<string> GetSelectedSignalLabels()
    {
        return AvailableSignals
            .Where(item => item.IsSelected)
            .Select(item => item.Label)
            .ToList();
    }

    private bool MatchesSignalSearch(SignalSelectionItem signal)
    {
        if (string.IsNullOrWhiteSpace(SignalSearchText))
        {
            return true;
        }

        return signal.Label.Contains(SignalSearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void SetSelectionForVisibleSignals(bool selected)
    {
        foreach (var signal in AvailableSignals.Where(MatchesSignalSearch))
        {
            signal.IsSelected = selected;
        }
    }

    private void ClearSignalSearch()
    {
        SignalSearchText = string.Empty;
    }

    private void RemoveSignalFromGroup(GroupSignalItem? item)
    {
        SelectedPlotGroup?.RemoveSignal(item);
    }

    private void RebuildPlots(bool preserveView = false)
    {
        var snapshots = preserveView ? CapturePanelSnapshots() : null;

        if (_dataset is null)
        {
            PlotPanels.Clear();
            _activeCursorPanel = null;
            _xAxisSyncService.Bind([]);
            CursorValueInfo = "Signaalwaarde: -";
            return;
        }

        var groups = CapturePlotGroups()
            .Where(group => group.Signals.Count > 0)
            .ToList();
        if (groups.Count == 0)
        {
            PlotPanels.Clear();
            _activeCursorPanel = null;
            _xAxisSyncService.Bind([]);
            CursorValueInfo = "Signaalwaarde: -";
            return;
        }

        var options = CaptureViewOptions();
        if (!options.UseDownsampling)
        {
            var oversized = groups.SelectMany(static group => group.Signals)
                .Distinct(StringComparer.Ordinal)
                .Select(label => _dataset.SignalSeriesByLabel.TryGetValue(label, out var series)
                    ? (Label: label, Count: CountVisible(series.Time, options.TimeStart, options.TimeEnd))
                    : (Label: label, Count: 0))
                .FirstOrDefault(static item => item.Count > 200_000);
            if (oversized.Count > 200_000)
            {
                PresetStatus = $"Volledige resolutie geblokkeerd: '{oversized.Label}' bevat {oversized.Count:N0} zichtbare punten. Zoom via begin/eindtijd in of schakel expliciet LOD in.";
                return;
            }
        }

        var panels = _plotModelBuilder.Build(_dataset, groups, options);

        PlotPanels.Clear();
        foreach (var panel in panels)
        {
            PlotPanels.Add(panel);
        }

        if (_activeCursorPanel is null || !PlotPanels.Contains(_activeCursorPanel))
        {
            _activeCursorPanel = PlotPanels.FirstOrDefault();
        }

        if (snapshots is not null)
        {
            RestorePanelSnapshots(snapshots);
        }

        ApplyAxisSyncConfiguration();
        _xAxisSyncService.Bind(PlotPanels.Select(panel => panel.PlotModel));
        RefreshCursorAnnotations();
    }

    private static int CountVisible(double[] times, double? start, double? end)
    {
        var count = 0;
        foreach (var time in times)
        {
            if ((!start.HasValue || time >= start.Value) && (!end.HasValue || time <= end.Value))
            {
                count++;
            }
        }

        return count;
    }

    private void TriggerLiveRebuild()
    {
        if (_suppressAutoRebuild || _dataset is null)
        {
            return;
        }

        RebuildPlots(preserveView: true);
    }

    private List<PanelViewSnapshot> CapturePanelSnapshots()
    {
        var snapshots = new List<PanelViewSnapshot>(PlotPanels.Count);
        foreach (var panel in PlotPanels)
        {
            var axisRanges = new Dictionary<string, AxisRange>(StringComparer.Ordinal);
            foreach (var axis in panel.PlotModel.Axes)
            {
                axisRanges[GetAxisId(axis)] = new AxisRange(axis.ActualMinimum, axis.ActualMaximum);
            }

            var visibility = panel.PlotModel.Series
                .Where(series => !string.IsNullOrWhiteSpace(series.Title))
                .ToDictionary(series => series.Title, series => series.IsVisible, StringComparer.Ordinal);

            snapshots.Add(new PanelViewSnapshot(axisRanges, visibility));
        }

        return snapshots;
    }

    private void RestorePanelSnapshots(IReadOnlyList<PanelViewSnapshot> snapshots)
    {
        var count = Math.Min(snapshots.Count, PlotPanels.Count);
        for (var i = 0; i < count; i++)
        {
            var panel = PlotPanels[i];
            var snapshot = snapshots[i];

            foreach (var axis in panel.PlotModel.Axes)
            {
                if (!snapshot.AxisRanges.TryGetValue(GetAxisId(axis), out var range))
                {
                    continue;
                }

                if (double.IsNaN(range.Minimum) || double.IsNaN(range.Maximum) || range.Maximum <= range.Minimum)
                {
                    continue;
                }

                axis.Zoom(range.Minimum, range.Maximum);
            }

            foreach (var series in panel.PlotModel.Series)
            {
                if (string.IsNullOrWhiteSpace(series.Title))
                {
                    continue;
                }

                if (snapshot.SeriesVisibility.TryGetValue(series.Title, out var visible))
                {
                    series.IsVisible = visible;
                }
            }

            panel.PlotModel.InvalidatePlot(false);
        }
    }

    private static string GetAxisId(Axis axis)
    {
        if (!string.IsNullOrWhiteSpace(axis.Key))
        {
            return $"key:{axis.Key}";
        }

        return $"{axis.Position}:{axis.PositionTier}";
    }

    private sealed record AxisRange(double Minimum, double Maximum);

    private sealed record PanelViewSnapshot(
        IReadOnlyDictionary<string, AxisRange> AxisRanges,
        IReadOnlyDictionary<string, bool> SeriesVisibility);

    private async Task ExportPresetAsync()
    {
        try
        {
            var filePath = _fileDialogService.SavePresetFile(null);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            var preset = new PlotPreset
            {
                PresetType = "can-log-viewer-layout",
                Version = 2,
                SavedAtUtc = DateTime.UtcNow,
                PlotGroups = CapturePlotGroups().ToList(),
                View = CaptureViewOptions()
            };

            if (_dataset is not null)
            {
                foreach (var group in preset.PlotGroups)
                {
                    group.SignalIdentities = group.Signals
                        .Where(_dataset.SignalSeriesByLabel.ContainsKey)
                        .Select(label => PresetSignalReference.From(_dataset.SignalSeriesByLabel[label]))
                        .ToList();
                }
            }

            var json = _presetSerializer.Serialize(preset);
            await File.WriteAllTextAsync(filePath, json);
            PresetStatus = $"Layout geëxporteerd: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Layout export failed");
            PresetStatus = $"Export mislukt: {ex.Message}";
        }
    }

    private async Task ImportPresetAsync()
    {
        try
        {
            var filePath = _fileDialogService.PickPresetFile(null);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var preset = _presetSerializer.Deserialize(json);
            var resolvedGroups = ResolvePresetGroups(preset);
            LoadPlotGroups(resolvedGroups);
            ApplyViewOptions(preset.View);

            var selectedLabels = resolvedGroups
                .SelectMany(group => group.Signals)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var signal in AvailableSignals)
            {
                signal.IsSelected = selectedLabels.Contains(signal.Label);
            }

            RebuildPlots();
            PresetStatus = $"Layout geïmporteerd: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Layout import failed");
            PresetStatus = $"Import mislukt: {ex.Message}";
        }
    }

    private IReadOnlyList<PlotGroup> ResolvePresetGroups(PlotPreset preset)
    {
        if (_dataset is null)
        {
            throw new InvalidOperationException("Laad eerst een dataset voordat je een preset toepast.");
        }

        var result = new List<PlotGroup>(preset.PlotGroups.Count);
        foreach (var source in preset.PlotGroups)
        {
            var resolvedLabels = new List<string>();
            if (preset.Version >= 2 && source.SignalIdentities.Count > 0)
            {
                foreach (var reference in source.SignalIdentities)
                {
                    var matches = _dataset.SignalSeriesByLabel.Values
                        .Where(series => series.Identity == reference.ToIdentity())
                        .Select(static series => series.Label)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (matches.Length != 1)
                    {
                        throw new InvalidDataException(
                            $"Preset-signaal '{reference.DisplayLabel}' heeft {matches.Length} exacte matches; de preset is niet toegepast.");
                    }

                    resolvedLabels.Add(matches[0]);
                }
            }
            else
            {
                foreach (var legacyLabel in source.Signals)
                {
                    var semanticName = legacyLabel.Split(" [", 2, StringSplitOptions.None)[0];
                    var matches = _dataset.SignalSeriesByLabel.Values
                        .Where(series => string.Equals(
                            $"{series.Identity.MessageName}.{series.Identity.SignalName}",
                            semanticName,
                            StringComparison.Ordinal))
                        .Select(static series => series.Label)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (matches.Length != 1)
                    {
                        throw new InvalidDataException(
                            $"V1-label '{legacyLabel}' heeft {matches.Length} mogelijke matches. Kies het juiste kanaal/signaal handmatig; er wordt niets gegokt.");
                    }

                    resolvedLabels.Add(matches[0]);
                }
            }

            var remappedOffsets = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var i = 0; i < resolvedLabels.Count; i++)
            {
                var legacy = i < source.Signals.Count ? source.Signals[i] : null;
                remappedOffsets[resolvedLabels[i]] = legacy is not null && source.Offsets.TryGetValue(legacy, out var offset)
                    ? offset
                    : 0d;
            }

            result.Add(new PlotGroup
            {
                Title = source.Title,
                Signals = resolvedLabels,
                SignalIdentities = source.SignalIdentities,
                Offsets = remappedOffsets,
                LockYAxis = source.LockYAxis
            });
        }

        return result;
    }

    private void OpenPlotsInWindow()
    {
        try
        {
            if (_dataset is null)
            {
                PresetStatus = "Geen dataset geladen om in apart venster te tonen.";
                return;
            }

            var groups = CapturePlotGroups()
                .Where(group => group.Signals.Count > 0)
                .ToList();
            if (groups.Count == 0)
            {
                PresetStatus = "Geen plotgroepen beschikbaar voor apart venster.";
                return;
            }

            var options = CaptureViewOptions();
            var detachedPanels = _plotModelBuilder.Build(_dataset, groups, options);
            _plotWindowService.ShowPlots(
                detachedPanels,
                options.SubplotHeight,
                options.MaxPointsPerTrace,
                options.UseDownsampling,
                LinkXAxisAcrossPanels,
                LinkYAxisAcrossPanels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening detached plot window failed.");
            PresetStatus = $"Apart plotvenster openen mislukt: {ex.Message}";
        }
    }

    private void ApplyGroupsFromUi()
    {
        RebuildPlots();
        if (AutoOpenDetachedOnApply)
        {
            OpenPlotsInWindow();
        }
    }

    public void SetCursorAt(double timeSeconds)
    {
        SetCursorAt(timeSeconds, panel: null);
    }

    public void SetCursorAt(double timeSeconds, PlotPanelModel? panel)
    {
        _activeCursorPanel = panel ?? _activeCursorPanel;
        if (double.IsNaN(timeSeconds) || double.IsInfinity(timeSeconds))
        {
            return;
        }

        var snappedTime = SnapTimeToNearestData(panel, timeSeconds, out var nearestLabel, out var nearestValue);
        CursorTime = snappedTime;
        CursorValueInfo = nearestLabel is null
            ? "Signaalwaarde: -"
            : $"Signaalwaarde: {ShortSignalLabel(nearestLabel)} = {nearestValue?.ToString("G17", CultureInfo.InvariantCulture)}";
        RefreshCursorAnnotations();
        UpdateCursorInfo();
    }

    public void SetFlagAAt(double timeSeconds)
    {
        SetFlagAAt(timeSeconds, panel: null);
    }

    public void SetFlagAAt(double timeSeconds, PlotPanelModel? panel)
    {
        _activeCursorPanel = panel ?? _activeCursorPanel;
        var snappedTime = SnapTimeToNearestData(panel, timeSeconds, out var nearestLabel, out var nearestValue);
        FlagATime = snappedTime;
        CursorTime = snappedTime;
        CursorValueInfo = nearestLabel is null
            ? "Signaalwaarde: -"
            : $"Signaalwaarde: {ShortSignalLabel(nearestLabel)} = {nearestValue?.ToString("G17", CultureInfo.InvariantCulture)}";
        RefreshCursorAnnotations();
        UpdateCursorInfo();
    }

    public void SetFlagBAt(double timeSeconds)
    {
        SetFlagBAt(timeSeconds, panel: null);
    }

    public void SetFlagBAt(double timeSeconds, PlotPanelModel? panel)
    {
        _activeCursorPanel = panel ?? _activeCursorPanel;
        var snappedTime = SnapTimeToNearestData(panel, timeSeconds, out var nearestLabel, out var nearestValue);
        FlagBTime = snappedTime;
        CursorTime = snappedTime;
        CursorValueInfo = nearestLabel is null
            ? "Signaalwaarde: -"
            : $"Signaalwaarde: {ShortSignalLabel(nearestLabel)} = {nearestValue?.ToString("G17", CultureInfo.InvariantCulture)}";
        RefreshCursorAnnotations();
        UpdateCursorInfo();
    }

    public void ResetAllPlots()
    {
        foreach (var panel in PlotPanels)
        {
            ResetPlotToDefaultView(panel.PlotModel);
            panel.PlotModel.InvalidatePlot(false);
        }

        ApplyAxisSyncConfiguration();
        _xAxisSyncService.Bind(PlotPanels.Select(panel => panel.PlotModel));
        RefreshCursorAnnotations();
    }

    private static void ResetPlotToDefaultView(PlotModel model)
    {
        foreach (var axis in model.Axes)
        {
            var dataMin = axis.DataMinimum;
            var dataMax = axis.DataMaximum;
            if (double.IsNaN(dataMin) || double.IsNaN(dataMax))
            {
                axis.Reset();
                continue;
            }

            var range = dataMax - dataMin;
            if (range <= 0 || double.IsNaN(range))
            {
                var magnitude = Math.Max(1.0, Math.Abs(dataMax));
                var pad = magnitude * 0.05;
                axis.Zoom(dataMin - pad, dataMax + pad);
                continue;
            }

            var padFactor = axis.Position == AxisPosition.Bottom ? 0.01 : 0.06;
            var padRange = range * padFactor;
            axis.Zoom(dataMin - padRange, dataMax + padRange);
        }
    }

    private void ApplyAxisSyncConfiguration()
    {
        _xAxisSyncService.Configure(syncXAxis: LinkXAxisAcrossPanels, syncYAxis: LinkYAxisAcrossPanels);
    }

    private void SetFlagAFromCursor()
    {
        if (CursorTime.HasValue)
        {
            SetFlagAAt(CursorTime.Value);
        }
    }

    private void SetFlagBFromCursor()
    {
        if (CursorTime.HasValue)
        {
            SetFlagBAt(CursorTime.Value);
        }
    }

    private void JumpFlagBToNextChange()
    {
        if (!FlagATime.HasValue)
        {
            PresetStatus = "Zet eerst Flag A.";
            return;
        }

        var next = FindNextChangeTime(FlagATime.Value);
        if (!next.HasValue)
        {
            PresetStatus = "Geen volgende verandering gevonden.";
            return;
        }

        SetFlagBAt(next.Value);
        PresetStatus = $"Flag B verplaatst naar volgende verandering op t={next.Value:G17}s.";
    }

    private void ClearFlags()
    {
        FlagATime = null;
        FlagBTime = null;
        CursorValueInfo = "Signaalwaarde: -";
        UpdateCursorInfo();
        RefreshCursorAnnotations();
    }

    private void UpdateCursorInfo()
    {
        var cursorText = CursorTime.HasValue ? $"{CursorTime.Value:G17}s" : "-";
        var aText = FlagATime.HasValue ? $"{FlagATime.Value:G17}s" : "-";
        var bText = FlagBTime.HasValue ? $"{FlagBTime.Value:G17}s" : "-";
        var dtText = FlagATime.HasValue && FlagBTime.HasValue
            ? $"{Math.Abs(FlagBTime.Value - FlagATime.Value):G17}s"
            : "-";
        CursorInfo = $"Cursor: {cursorText} | A: {aText} | B: {bText} | dt: {dtText}";
    }

    private double SnapTimeToNearestData(
        PlotPanelModel? panel,
        double requestedTime,
        out string? nearestLabel,
        out double? nearestValue)
    {
        nearestLabel = null;
        nearestValue = null;
        var bestTime = requestedTime;
        var bestDistance = double.MaxValue;

        var panels = panel is null ? PlotPanels : [panel];
        foreach (var currentPanel in panels)
        {
            foreach (var series in currentPanel.SeriesData)
            {
                if (series.Time.Length == 0 || series.Value.Length == 0)
                {
                    continue;
                }

                var nearestIndex = FindNearestIndex(series.Time, requestedTime);
                if (nearestIndex < 0 || nearestIndex >= series.Time.Length)
                {
                    continue;
                }

                var candidateTime = series.Time[nearestIndex];
                var distance = Math.Abs(candidateTime - requestedTime);
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestTime = candidateTime;
                nearestLabel = series.Label;
                nearestValue = series.Value[nearestIndex];
            }
        }

        return bestTime;
    }

    private static int FindNearestIndex(double[] values, double target)
    {
        if (values.Length == 0)
        {
            return -1;
        }

        var upper = UpperBound(values, target);
        if (upper <= 0)
        {
            return 0;
        }

        if (upper >= values.Length)
        {
            return values.Length - 1;
        }

        var lowerIndex = upper - 1;
        var upperIndex = upper;
        var lowerDistance = Math.Abs(values[lowerIndex] - target);
        var upperDistance = Math.Abs(values[upperIndex] - target);
        return lowerDistance <= upperDistance ? lowerIndex : upperIndex;
    }

    private static string ShortSignalLabel(string label)
    {
        var dotIndex = label.IndexOf('.');
        if (dotIndex < 0 || dotIndex + 1 >= label.Length)
        {
            return label;
        }

        var bracketIndex = label.IndexOf(" [", StringComparison.Ordinal);
        if (bracketIndex <= dotIndex)
        {
            return label[(dotIndex + 1)..];
        }

        return label[(dotIndex + 1)..bracketIndex];
    }

    private double? FindNextChangeTime(double fromTime)
    {
        double? best = null;
        const double eps = 1e-9;

        foreach (var panel in PlotPanels)
        {
            foreach (var series in panel.SeriesData)
            {
                if (series.Time.Length < 2 || series.Value.Length < 2)
                {
                    continue;
                }

                var start = UpperBound(series.Time, fromTime);
                if (start <= 0)
                {
                    start = 1;
                }

                for (var i = start; i < series.Time.Length; i++)
                {
                    if (Math.Abs(series.Value[i] - series.Value[i - 1]) <= eps)
                    {
                        continue;
                    }

                    var candidate = series.Time[i];
                    if (!best.HasValue || candidate < best.Value)
                    {
                        best = candidate;
                    }

                    break;
                }
            }
        }

        return best;
    }

    private static int UpperBound(double[] values, double threshold)
    {
        var lo = 0;
        var hi = values.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (values[mid] <= threshold)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private void RefreshCursorAnnotations()
    {
        foreach (var panel in PlotPanels)
        {
            var model = panel.PlotModel;
            var removable = model.Annotations
                .Where(annotation =>
                    string.Equals(annotation.Tag as string, "cursor-main", StringComparison.Ordinal) ||
                    string.Equals(annotation.Tag as string, "cursor-a", StringComparison.Ordinal) ||
                    string.Equals(annotation.Tag as string, "cursor-b", StringComparison.Ordinal) ||
                    string.Equals(annotation.Tag as string, "cursor-value-point", StringComparison.Ordinal) ||
                    string.Equals(annotation.Tag as string, "cursor-value-text", StringComparison.Ordinal))
                .ToList();

            foreach (var annotation in removable)
            {
                model.Annotations.Remove(annotation);
            }

            if (CursorTime.HasValue)
            {
                var cursorTime = CursorTime.Value;
                var visibleSeries = model.Series
                    .Where(series => series.IsVisible && !string.IsNullOrWhiteSpace(series.Title))
                    .Select(series => series.Title)
                    .ToHashSet(StringComparer.Ordinal);

                model.Annotations.Add(new LineAnnotation
                {
                    Tag = "cursor-main",
                    Type = LineAnnotationType.Vertical,
                    X = cursorTime,
                    Color = OxyColor.FromAColor(160, OxyColors.DimGray),
                    LineStyle = LineStyle.Dot,
                    StrokeThickness = 1,
                    Text = "C",
                    TextColor = OxyColors.DimGray
                });

                var drawnLabels = 0;
                const int maxLabelsPerPanel = 8;
                var shouldDrawValueLabels = ReferenceEquals(panel, _activeCursorPanel);
                foreach (var series in panel.SeriesData)
                {
                    if (!shouldDrawValueLabels)
                    {
                        break;
                    }

                    if (drawnLabels >= maxLabelsPerPanel)
                    {
                        break;
                    }

                    if (!visibleSeries.Contains(series.Label))
                    {
                        continue;
                    }

                    if (series.Time.Length == 0 || series.Value.Length == 0)
                    {
                        continue;
                    }

                    var index = FindNearestIndex(series.Time, cursorTime);
                    if (index < 0 || index >= series.Time.Length)
                    {
                        continue;
                    }

                    var x = series.Time[index];
                    var y = series.Value[index];
                    var label = $"{ShortSignalLabel(series.Label)}={y.ToString("G17", CultureInfo.InvariantCulture)}";

                    model.Annotations.Add(new PointAnnotation
                    {
                        Tag = "cursor-value-point",
                        X = x,
                        Y = y,
                        YAxisKey = series.YAxisKey,
                        Shape = MarkerType.Circle,
                        Fill = series.Color,
                        Stroke = OxyColors.Black,
                        StrokeThickness = 0.8,
                        Size = 3.5
                    });

                    model.Annotations.Add(new TextAnnotation
                    {
                        Tag = "cursor-value-text",
                        Text = label,
                        TextPosition = new DataPoint(x, y),
                        YAxisKey = series.YAxisKey,
                        Stroke = OxyColor.FromAColor(40, OxyColors.Black),
                        Background = OxyColor.FromAColor(180, OxyColors.White),
                        TextColor = series.Color,
                        FontWeight = FontWeights.Bold,
                        FontSize = 10
                    });
                    drawnLabels++;
                }
            }

            if (FlagATime.HasValue)
            {
                model.Annotations.Add(new LineAnnotation
                {
                    Tag = "cursor-a",
                    Type = LineAnnotationType.Vertical,
                    X = FlagATime.Value,
                    Color = OxyColors.OrangeRed,
                    LineStyle = LineStyle.Solid,
                    StrokeThickness = 1.5,
                    Text = "A",
                    TextColor = OxyColors.OrangeRed
                });
            }

            if (FlagBTime.HasValue)
            {
                model.Annotations.Add(new LineAnnotation
                {
                    Tag = "cursor-b",
                    Type = LineAnnotationType.Vertical,
                    X = FlagBTime.Value,
                    Color = OxyColors.DodgerBlue,
                    LineStyle = LineStyle.Solid,
                    StrokeThickness = 1.5,
                    Text = "B",
                    TextColor = OxyColors.DodgerBlue
                });
            }

            model.InvalidatePlot(false);
        }
    }
}
