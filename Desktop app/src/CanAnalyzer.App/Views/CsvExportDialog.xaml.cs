using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Export;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Views;

public partial class CsvExportDialog : Window
{
    private readonly CanDataset _dataset;
    private readonly HashSet<string> _analysisSelection;
    private CancellationTokenSource? _previewCts;
    private bool _ready;
    public IReadOnlyList<CsvExportChoice<SignalIdentity>> Signals { get; }
    public IReadOnlyList<CsvExportChoice<string>> Columns { get; }
    public ICollectionView VisibleSignals { get; }
    public CsvExportOptions? Options { get; private set; }

    public CsvExportDialog(CanDataset dataset, IEnumerable<string> selectedLabels)
    {
        _dataset = dataset;
        _analysisSelection = selectedLabels.ToHashSet(StringComparer.Ordinal);
        Signals = dataset.SignalSeriesByIdentity.Keys.OrderBy(s => s.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .Select(s => new CsvExportChoice<SignalIdentity>(s, s.DisplayLabel)).ToArray();
        Columns = CsvExportOptions.AvailableColumns
            .Select(c => new CsvExportChoice<string>(c.Name, $"{c.Name} — {c.Description}")).ToArray();
        VisibleSignals = CollectionViewSource.GetDefaultView(Signals);
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        DataContext = this;
        DatasetNotice.Text = (dataset.Completeness == DatasetCompleteness.Complete ? "" : "Let op: deze dataset is PARTIAL; de export bevat onvolledige meetdata. ") +
            (dataset.StartTimeUtc is null ? "Absolute meettijd ontbreekt: timestamp_utc en unix_time_ns blijven leeg." : "Absolute meettijd is beschikbaar als UTC.");
        foreach (var choice in Signals) choice.PropertyChanged += ChoiceChanged;
        foreach (var choice in Columns) choice.PropertyChanged += ChoiceChanged;
        Closed += (_, _) => _previewCts?.Cancel();
        _ready = true;
        InvalidatePreview();
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
