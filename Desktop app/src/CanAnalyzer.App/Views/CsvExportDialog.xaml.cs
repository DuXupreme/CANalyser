using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Export;
using CanAnalyzer.App.State;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Views;

public partial class CsvExportDialog : Window
{
    private readonly CanDataset _dataset;
    private readonly HashSet<string> _analysisSelection;
    private CancellationTokenSource? _previewCts;
    private bool _ready;
    private bool _savingPreset;
    private readonly Func<IReadOnlyList<CsvExportPreset>, Task>? _savePresets;
    private SignalIdentity[] _missingPresetSignals = [];
    public ObservableCollection<CsvExportPreset> Presets { get; }
    public IReadOnlyList<CsvExportChoice<SignalIdentity>> Signals { get; }
    public ObservableCollection<CsvExportChoice<string>> Columns { get; }
    public ICollectionView VisibleSignals { get; }
    public CsvExportOptions? Options { get; private set; }

    public CsvExportDialog(CanDataset dataset, IEnumerable<string> selectedLabels,
        IEnumerable<CsvExportPreset>? presets = null,
        Func<IReadOnlyList<CsvExportPreset>, Task>? savePresets = null)
    {
        _dataset = dataset;
        _savePresets = savePresets;
        Presets = new(presets ?? []);
        _analysisSelection = selectedLabels.ToHashSet(StringComparer.Ordinal);
        Signals = dataset.SignalSeriesByIdentity.Keys.OrderBy(s => s.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .Select(s => new CsvExportChoice<SignalIdentity>(s, s.DisplayLabel)).ToArray();
        Columns = new(CsvExportOptions.AvailableColumns
            .Select(c => new CsvExportChoice<string>(c.Name, $"{c.Name} — {c.Description}")));
        VisibleSignals = CollectionViewSource.GetDefaultView(Signals);
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        DataContext = this;
        DatasetNotice.Text = (dataset.Completeness == DatasetCompleteness.Complete ? "" : "Let op: deze dataset is PARTIAL; de export bevat onvolledige meetdata. ") +
            (dataset.StartTimeUtc is null ? "Absolute meettijd ontbreekt: timestamp_utc en unix_time_ns blijven leeg." : "Absolute meettijd is beschikbaar als UTC.");
        foreach (var choice in Signals) choice.PropertyChanged += ChoiceChanged;
        foreach (var choice in Columns) choice.PropertyChanged += ChoiceChanged;
        Columns.CollectionChanged += (_, _) => InvalidatePreview();
        Closed += (_, _) => _previewCts?.Cancel();
        Closing += (_, e) => { if (_savingPreset) e.Cancel = true; };
        _ready = true;
        InvalidatePreview();
    }

    private void PresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PresetList.SelectedItem is not CsvExportPreset preset) return;
        ApplyPreset(preset);
    }

    public void ApplyPreset(CsvExportPreset preset)
    {
        _ready = false;
        try
        {
            PresetName.Text = preset.Name;
            SignalSearch.Text = string.Empty;
            VisibleSignals.Filter = null;
            var options = preset.Options;
            var selected = options.Signals?.ToHashSet();
            foreach (var signal in Signals) signal.IsSelected = selected is null || selected.Contains(signal.Value);
            _missingPresetSignals = selected?.Except(Signals.Select(s => s.Value)).ToArray() ?? [];
            var ordered = options.Columns.Distinct(StringComparer.Ordinal).ToArray();
            var known = Columns.ToDictionary(c => c.Value, StringComparer.Ordinal);
            var reordered = ordered.Where(known.ContainsKey).Select(c => known[c])
                .Concat(Columns.Where(c => !ordered.Contains(c.Value, StringComparer.Ordinal))).ToArray();
            Columns.Clear();
            foreach (var column in reordered)
            {
                column.IsSelected = ordered.Contains(column.Value, StringComparer.Ordinal);
                Columns.Add(column);
            }
            TimeMode.SelectedIndex = Enum.IsDefined(options.TimeOrigin) ? (int)options.TimeOrigin : 0;
            Delimiter.SelectedIndex = options.Delimiter switch { ";" => 1, "\t" => 2, _ => 0 };
            Decimals.SelectedIndex = options.DecimalSeparator == "," ? 1 : 0;
            Utf8Bom.IsChecked = options.IncludeUtf8Bom;
            var unknownColumns = ordered.Where(c => !known.ContainsKey(c)).ToArray();
            PresetStatus.Text = _missingPresetSignals.Length == 0 ? "Preset geladen." :
                $"{_missingPresetSignals.Length} presetsignaal/signalen ontbreken in deze log en worden niet geëxporteerd. Ze blijven bij bijwerken in de preset bewaard.";
            if (unknownColumns.Length > 0) PresetStatus.Text += " Onbekende kolommen overgeslagen: " + string.Join(", ", unknownColumns);
            PresetStatus.ToolTip = string.Join("\n", _missingPresetSignals.Select(s => s.DisplayLabel));
        }
        finally { _ready = true; InvalidatePreview(); }
    }

    private void MoveColumnUp(object sender, RoutedEventArgs e) => MoveColumn(sender, -1);
    private void MoveColumnDown(object sender, RoutedEventArgs e) => MoveColumn(sender, 1);
    private void MoveColumn(object sender, int direction)
    {
        if (sender is not Button { Tag: CsvExportChoice<string> column }) return;
        var from = Columns.IndexOf(column);
        var to = from + direction;
        if (from < 0 || to < 0 || to >= Columns.Count) return;
        Columns.Move(from, to);
        ColumnList.ScrollIntoView(column);
        InvalidatePreview();
    }

    private void PresetNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) UpdatePresetButtons();
    }

    private void UpdatePresetButtons()
    {
        var valid = !_savingPreset && _savePresets is not null &&
                    !string.IsNullOrWhiteSpace(PresetName.Text) &&
                    Signals.Any(s => s.IsSelected) && Columns.Any(c => c.IsSelected);
        SaveNewPresetButton.IsEnabled = valid;
        UpdatePresetButton.IsEnabled = valid && PresetList.SelectedItem is CsvExportPreset;
        DeletePresetButton.IsEnabled = !_savingPreset && _savePresets is not null && PresetList.SelectedItem is CsvExportPreset;
    }

    private async void SaveNewPreset(object sender, RoutedEventArgs e) => await SavePresetAsync(false);
    private async void UpdatePreset(object sender, RoutedEventArgs e) => await SavePresetAsync(true);

    public async Task<bool> SavePresetAsync(bool update)
    {
        if (_savingPreset || _savePresets is null) return false;
        var existing = update ? PresetList.SelectedItem as CsvExportPreset : null;
        var name = PresetName.Text.Trim();
        if (name.Length == 0 || !Signals.Any(s => s.IsSelected) || !Columns.Any(c => c.IsSelected) || (update && existing is null)) return false;
        if (Presets.Any(p => p.Id != existing?.Id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            PresetStatus.Text = "Deze naam bestaat al. Kies die preset en gebruik Bijwerken, of voer een andere naam in.";
            return false;
        }
        var options = CaptureOptions();
        if (update) options = options with { Signals = options.Signals!.Concat(_missingPresetSignals).Distinct().ToArray() };
        var preset = new CsvExportPreset { Id = existing?.Id ?? Guid.NewGuid(), Name = name, Options = options };
        var next = Presets.ToList();
        if (existing is null) next.Add(preset); else next[next.FindIndex(p => p.Id == existing.Id)] = preset;
        if (!await PersistPresetsAsync(next)) return false;
        Presets.Clear();
        foreach (var item in next) Presets.Add(item);
        PresetList.SelectedItem = preset;
        PresetStatus.Text = _missingPresetSignals.Length == 0 ? "Preset opgeslagen op deze computer." :
            $"Preset opgeslagen, inclusief {_missingPresetSignals.Length} signaal/signalen die in deze log ontbreken.";
        return true;
    }

    private async void DeletePreset(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not CsvExportPreset preset || _savingPreset) return;
        if (MessageBox.Show(this, $"Preset ‘{preset.Name}’ verwijderen?", "CSV-preset verwijderen",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!await DeletePresetAsync(preset.Id)) return;
        PresetStatus.Text = "Preset verwijderd. De huidige exportselectie blijft staan.";
    }

    public async Task<bool> DeletePresetAsync(Guid id)
    {
        if (_savingPreset || _savePresets is null || !Presets.Any(p => p.Id == id)) return false;
        var next = Presets.Where(p => p.Id != id).ToList();
        if (!await PersistPresetsAsync(next)) return false;
        Presets.Clear();
        foreach (var item in next) Presets.Add(item);
        PresetName.Text = string.Empty;
        _missingPresetSignals = [];
        UpdatePresetButtons();
        return true;
    }

    private async Task<bool> PersistPresetsAsync(IReadOnlyList<CsvExportPreset> next)
    {
        _savingPreset = true;
        PresetControls.IsEnabled = false;
        UpdatePresetButtons();
        try { await _savePresets!(next); return true; }
        catch (Exception ex) { PresetStatus.Text = "Preset opslaan mislukt: " + ex.Message; return false; }
        finally { _savingPreset = false; PresetControls.IsEnabled = true; UpdatePresetButtons(); }
    }

    private void ChoiceChanged(object? sender, PropertyChangedEventArgs e) => InvalidatePreview();
    private void SettingsChanged(object sender, RoutedEventArgs e) => InvalidatePreview();
    private void InvalidatePreview()
    {
        if (!_ready) return;
        _previewCts?.Cancel();
        var count = Signals.Count(s => s.IsSelected);
        var columnCount = Columns.Count(c => c.IsSelected);
        SelectionSummary.Text = $"{count} / {Signals.Count} signalen · {columnCount} kolommen";
        ExportButton.IsEnabled = PreviewButton.IsEnabled = count > 0 && columnCount > 0;
        PreviewText.Text = "Instellingen gewijzigd. Klik op Voorbeeld vernieuwen.";
        UpdatePresetButtons();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        var query = SignalSearch.Text.Trim();
        VisibleSignals.Filter = item => ((CsvExportChoice<SignalIdentity>)item).Label.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    private void SelectAllSignals(object sender, RoutedEventArgs e) => SetSignals(_ => true);
    private void SelectNoSignals(object sender, RoutedEventArgs e) => SetSignals(_ => false);
    private void SelectAnalysisSignals(object sender, RoutedEventArgs e) => SetSignals(s => _analysisSelection.Contains(s.Label));
    private void SelectVisibleSignals(object sender, RoutedEventArgs e) =>
        SetSignals(s => s.Label.Contains(SignalSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase));
    private void SetSignals(Func<CsvExportChoice<SignalIdentity>, bool> select)
    {
        _ready = false;
        foreach (var signal in Signals) signal.IsSelected = select(signal);
        _ready = true;
        InvalidatePreview();
    }
    private void SelectCompactColumns(object sender, RoutedEventArgs e) => SetColumns(CsvExportOptions.CompactColumns.Contains);
    private void SelectAllColumns(object sender, RoutedEventArgs e) => SetColumns(_ => true);
    private void SelectNoColumns(object sender, RoutedEventArgs e) => SetColumns(_ => false);
    private void SetColumns(Func<string, bool> select)
    {
        _ready = false;
        foreach (var column in Columns) column.IsSelected = select(column.Value);
        _ready = true;
        InvalidatePreview();
    }
    private void UseSoftwareFormat(object sender, RoutedEventArgs e)
    {
        Delimiter.SelectedIndex = 0; Decimals.SelectedIndex = 0; Utf8Bom.IsChecked = false;
    }
    private void UseExcelFormat(object sender, RoutedEventArgs e)
    {
        Delimiter.SelectedIndex = 1; Decimals.SelectedIndex = 1; Utf8Bom.IsChecked = true;
    }

    public CsvExportOptions CaptureOptions() => new()
    {
        Signals = Signals.Where(s => s.IsSelected).Select(s => s.Value).ToArray(),
        Columns = Columns.Where(c => c.IsSelected).Select(c => c.Value).ToArray(),
        TimeOrigin = (CsvTimeOrigin)TimeMode.SelectedIndex,
        Delimiter = Delimiter.SelectedIndex switch { 1 => ";", 2 => "\t", _ => "," },
        DecimalSeparator = Decimals.SelectedIndex == 1 ? "," : ".",
        IncludeUtf8Bom = Utf8Bom.IsChecked == true
    };

    private async void RefreshPreview(object sender, RoutedEventArgs e)
    {
        _previewCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _previewCts = cts;
        var options = CaptureOptions();
        PreviewButton.IsEnabled = false;
        PreviewText.Text = "Voorbeeld wordt gelezen…";
        try
        {
            var text = await Task.Run(() => CsvExportService.PreviewAsync(_dataset, options, cts.Token));
            if (!cts.IsCancellationRequested) PreviewText.Text = text;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) PreviewText.Text = "Voorbeeld mislukt: " + ex.Message; }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                _previewCts = null;
                PreviewButton.IsEnabled = ExportButton.IsEnabled;
            }
        }
    }

    private void Accept(object sender, RoutedEventArgs e)
    {
        Options = CaptureOptions();
        DialogResult = true;
    }
}

public sealed class CsvExportChoice<T>(T value, string label) : ObservableObject
{
    public T Value { get; } = value;
    public string Label { get; } = label;
    private bool _isSelected = true;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
