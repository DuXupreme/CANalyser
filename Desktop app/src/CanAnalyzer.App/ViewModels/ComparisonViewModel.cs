using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CanAnalyzer.Core.Analysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace CanAnalyzer.App.ViewModels;

public sealed class ComparisonBundleRow(AnalysisComparisonBundle bundle) : ObservableObject
{
    private bool _included = true;
    public AnalysisComparisonBundle Bundle { get; } = bundle;
    public bool Included { get => _included; set => SetProperty(ref _included, value); }
    public string Name => Bundle.Name;
    public string Machine => string.IsNullOrWhiteSpace(Bundle.MachineId) ? "Onbekend" : Bundle.MachineId;
    public string Period => Bundle.CanAlignUtc ? Bundle.FirstUtc!.Value.ToString("yyyy-MM-dd HH:mm 'UTC'") : "Relatieve opnametijd";
    public string Quality => $"{Bundle.Completeness}; klok {Bundle.ClockQuality}; schema {Bundle.SchemaVersion}";
    public string Notes => string.Join(" ", Bundle.Notes);
}

public sealed record ComparisonMetricChoice(string Key, string Label);
public sealed record ComparisonResultRow(string Bundle, string Machine, string Value, string Unit, string Basis,
    string Minimum, string Maximum, string Explanation);

public sealed partial class ComparisonViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<string>> _pickFiles;
    private readonly Func<string, CancellationToken, AnalysisComparisonBundle> _readBundle;
    private CancellationTokenSource? _loading;
    private bool _updatingChoices;
    private bool _disposed;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Open meerdere analyse-ZIP-bestanden om statistieken en trends te vergelijken.";
    [ObservableProperty] private string _filterStatus = "Nog geen analysepakketten geopend.";
    [ObservableProperty] private string _selectedMachine = "Alle machines";
    [ObservableProperty] private DateTime? _periodStart;
    [ObservableProperty] private DateTime? _periodEnd;
    [ObservableProperty] private ComparisonMetricChoice? _selectedMetric;
    [ObservableProperty] private bool _useVerifiedUtc;
    [ObservableProperty] private string _aggregateValue = "Niet beschikbaar";
    [ObservableProperty] private string _aggregateExplanation = "Selecteer pakketten en een uitkomst.";
    [ObservableProperty] private string _axisExplanation = "Relatieve tijd vanaf het eerste meetpunt van elk pakket.";
    [ObservableProperty] private PlotModel _trend = new() { Title = "Minuuttrends" };
    public ObservableCollection<ComparisonBundleRow> Bundles { get; } = [];
    public ObservableCollection<string> Machines { get; } = ["Alle machines"];
    public ObservableCollection<ComparisonMetricChoice> Metrics { get; } = [];
    public ObservableCollection<ComparisonResultRow> Results { get; } = [];
    public IAsyncRelayCommand OpenBundlesCommand { get; }
    public IRelayCommand ClearBundlesCommand { get; }
    public IRelayCommand CancelLoadingCommand { get; }
    public bool CanUseVerifiedUtc => VisibleBundles().Any() && VisibleBundles().All(row => row.Bundle.CanAlignUtc);

    public ComparisonViewModel(Func<IReadOnlyList<string>>? pickFiles = null,
        Func<string, CancellationToken, AnalysisComparisonBundle>? readBundle = null)
    {
        _readBundle = readBundle ?? AnalysisBundleComparisonReader.Read;
        _pickFiles = pickFiles ?? (() =>
        {
            var dialog = new OpenFileDialog { Title = "Analyse-uitvoer vergelijken", Filter = "CANalyser analysepakket (*.zip)|*.zip", Multiselect = true };
            return dialog.ShowDialog() == true ? dialog.FileNames : [];
        });
        OpenBundlesCommand = new AsyncRelayCommand(() => _disposed ? Task.CompletedTask : LoadBundlesAsync(_pickFiles()), () => !_disposed && !IsBusy);
        ClearBundlesCommand = new RelayCommand(Clear, () => !_disposed && !IsBusy);
        CancelLoadingCommand = new RelayCommand(() => _loading?.Cancel(), () => !_disposed && IsBusy);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OpenBundlesCommand.NotifyCanExecuteChanged(); ClearBundlesCommand.NotifyCanExecuteChanged(); CancelLoadingCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedMachineChanged(string value) => RebuildChoices();
    partial void OnPeriodStartChanged(DateTime? value) => RebuildChoices();
    partial void OnPeriodEndChanged(DateTime? value) => RebuildChoices();
    partial void OnSelectedMetricChanged(ComparisonMetricChoice? value) { if (!_updatingChoices) RefreshComparison(); }
    partial void OnUseVerifiedUtcChanged(bool value) => RefreshComparison();

    public async Task LoadBundlesAsync(IEnumerable<string> paths)
    {
        if (_disposed || IsBusy) return;
        var selected = paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => Bundles.All(row => !string.Equals(row.Bundle.Path, path, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selected.Length == 0) return;
        if (selected.Length + Bundles.Count > 32) { Status = "Vergelijk maximaal 32 pakketten tegelijk; verwijder eerst een selectie."; return; }
        using var cancellation = new CancellationTokenSource();
        _loading = cancellation; IsBusy = true;
        var errors = new List<string>(); var count = 0;
        try
        {
            foreach (var path in selected)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Status = $"Analysepakket lezen ({++count}/{selected.Length}): {System.IO.Path.GetFileName(path)}";
                try
                {
                    var bundle = await Task.Run(() => _readBundle(path, cancellation.Token), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_disposed) return;
                    var row = new ComparisonBundleRow(bundle);
                    row.PropertyChanged += OnBundleSelectionChanged;
                    Bundles.Add(row);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or
                    Microsoft.VisualBasic.FileIO.MalformedLineException or ArgumentException or InvalidOperationException)
                { errors.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_disposed) Status = errors.Count == 0 ? $"{Bundles.Count} pakketten geladen. Bronbestanden zijn ongewijzigd."
                : string.Join("\n", errors);
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "Lezen geannuleerd; reeds geopende pakketten blijven beschikbaar."; }
        finally
        {
            _loading = null; IsBusy = false;
            if (!_disposed)
            {
                var previousMachine = SelectedMachine;
                Machines.Clear(); Machines.Add("Alle machines");
                foreach (var machine in Bundles.Select(row => row.Machine).Distinct().Order()) Machines.Add(machine);
                SelectedMachine = Machines.Contains(previousMachine) ? previousMachine : "Alle machines";
                RebuildChoices();
            }
        }
    }

    private void Clear()
    {
        foreach (var row in Bundles) row.PropertyChanged -= OnBundleSelectionChanged;
        Bundles.Clear(); Machines.Clear(); Machines.Add("Alle machines"); SelectedMachine = "Alle machines";
        PeriodStart = null; PeriodEnd = null;
        RebuildChoices(); Status = "Vergelijking leeggemaakt; ZIP-bestanden zijn behouden.";
    }
    private void OnBundleSelectionChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(ComparisonBundleRow.Included)) RebuildChoices(); }

    private IEnumerable<ComparisonBundleRow> VisibleBundles()
    {
        if (PeriodStart.HasValue && PeriodEnd.HasValue && PeriodEnd.Value.Date < PeriodStart.Value.Date) return [];
        return Bundles.Where(row => row.Included && (SelectedMachine == "Alle machines" || row.Machine == SelectedMachine))
            .Where(row => (!PeriodStart.HasValue && !PeriodEnd.HasValue) || row.Bundle.CanAlignUtc)
            .Where(row => !PeriodStart.HasValue || row.Bundle.LastUtc >= new DateTimeOffset(DateTime.SpecifyKind(PeriodStart.Value.Date, DateTimeKind.Unspecified), TimeSpan.Zero))
            .Where(row => !PeriodEnd.HasValue || row.Bundle.FirstUtc < new DateTimeOffset(DateTime.SpecifyKind(PeriodEnd.Value.Date.AddDays(1), DateTimeKind.Unspecified), TimeSpan.Zero));
    }
    private void RebuildChoices()
    {
        if (_disposed) return;
        var key = SelectedMetric?.Key;
        _updatingChoices = true;
        try
        {
            Metrics.Clear();
            foreach (var metric in VisibleBundles().SelectMany(row => row.Bundle.Metrics).GroupBy(metric => metric.Key)
                         .Select(group => group.First()).OrderBy(metric => metric.Label))
                Metrics.Add(new(metric.Key, metric.Label));
            SelectedMetric = Metrics.FirstOrDefault(metric => metric.Key == key) ?? Metrics.FirstOrDefault();
        }
        finally { _updatingChoices = false; }
        OnPropertyChanged(nameof(CanUseVerifiedUtc));
        if (!CanUseVerifiedUtc) UseVerifiedUtc = false;
        RefreshComparison();
    }
    private void RefreshComparison()
    {
        if (_disposed || _updatingChoices) return;
        var rows = VisibleBundles().ToArray(); Results.Clear();
        FilterStatus = Bundles.Count == 0 ? "Nog geen analysepakketten geopend."
            : !Bundles.Any(row => row.Included) ? "Geen pakketten meegenomen. Vink hierboven 'Meenemen' aan om te vergelijken."
            : PeriodStart.HasValue && PeriodEnd.HasValue && PeriodEnd.Value.Date < PeriodStart.Value.Date
                ? "Ongeldig datumfilter: de einddatum ligt vóór de begindatum. Pas de datums aan om pakketten te vergelijken."
            : rows.Length == 0
                ? "Geen pakketten passen bij de huidige filters. Pas de machine- of datumfilters aan; het datumfilter vereist een bevestigde UTC-datum."
                : $"{rows.Length} van {Bundles.Count} geladen pakketten in deze vergelijking.";
        var key = SelectedMetric?.Key;
        foreach (var row in rows)
        {
            var metric = row.Bundle.Metrics.FirstOrDefault(item => item.Key == key);
            var eventBased = metric?.Aggregation is ComparisonAggregation.EventMean or ComparisonAggregation.EventP95;
            Results.Add(new(row.Name, row.Machine, Format(metric?.Value), metric?.Unit ?? "",
                metric is null ? "—" : metric.Aggregation == ComparisonAggregation.None ? "Per opname"
                    : Format(metric.Denominator) + (eventBased ? " events" : " s"),
                Format(metric?.Minimum), Format(metric?.Maximum), metric?.Note ?? "Uitkomst ontbreekt in dit pakket."));
        }
        var aggregate = key is null ? new ComparisonAggregate(null, "", 0, 0, 0, "Selecteer pakketten en een uitkomst.")
            : AnalysisBundleComparison.Aggregate(rows.Select(row => row.Bundle).ToArray(), key);
        AggregateValue = Format(aggregate.Value) + (aggregate.Value.HasValue ? " " + aggregate.Unit : "");
        AggregateExplanation = aggregate.Explanation;
        var calendar = UseVerifiedUtc && rows.Length > 0 && rows.All(row => row.Bundle.CanAlignUtc);
        AxisExplanation = calendar ? "Kalenderas in UTC; uitsluitend pakketten met bevestigde loggerklok."
            : "Relatieve tijd vanaf het eerste meetpunt van elk pakket; onbevestigde loggerdatums worden niet uitgelijnd.";
        if (PeriodStart.HasValue || PeriodEnd.HasValue)
            AxisExplanation += " Datumfilter selecteert gehele pakketten op overlap in bevestigde UTC; onbevestigde datums vallen buiten dit filter. Er vindt geen uitsnede of herberekening binnen een pakket plaats.";
        if (rows.SelectMany(row => row.Bundle.Metrics).Any(metric => metric.Key == key && metric.Trend.Count > 2000))
            AxisExplanation += " De grafiek toont maximaal 2.000 min/max-punten per pakket; statistieken gebruiken de volledige meetbasis.";
        var model = new PlotModel { Title = SelectedMetric?.Label ?? "Minuuttrends", IsLegendVisible = true };
        model.Legends.Add(new Legend { LegendPosition = LegendPosition.TopLeft, LegendPlacement = LegendPlacement.Outside,
            LegendOrientation = LegendOrientation.Horizontal });
        var units = rows.SelectMany(row => row.Bundle.Metrics).Where(metric => metric.Key == key)
            .Select(metric => metric.Unit).Distinct(StringComparer.Ordinal).ToArray();
        if (units.Length > 1)
        {
            model.Subtitle = "Verschillende eenheden: geen gezamenlijke trendas beschikbaar.";
            AxisExplanation += " De tabel behoudt elke oorspronkelijke eenheid; sluit een pakket uit om trends met dezelfde eenheid te bekijken.";
            Trend = model;
            return;
        }
        model.Axes.Add(calendar ? new DateTimeAxis { Position = AxisPosition.Bottom, Title = "UTC", StringFormat = "dd-MM HH:mm" }
            : new LinearAxis { Position = AxisPosition.Bottom, Title = "Tijd sinds eerste meetpunt [min]" });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = rows.SelectMany(row => row.Bundle.Metrics).FirstOrDefault(metric => metric.Key == key)?.Unit ?? "" });
        var colors = new[] { OxyColors.SteelBlue, OxyColors.DarkOrange, OxyColors.SeaGreen, OxyColors.Purple, OxyColors.IndianRed, OxyColors.Teal };
        var color = 0;
        foreach (var row in rows)
        {
            var metric = row.Bundle.Metrics.FirstOrDefault(item => item.Key == key);
            if (metric is null || metric.Trend.Count == 0) continue;
            var line = new LineSeries { Title = row.Name, Color = colors[color++ % colors.Length], StrokeThickness = 1.5, MarkerType = MarkerType.Circle, MarkerSize = 2 };
            double? previousEnd = null;
            foreach (var point in metric.DisplayTrend ?? metric.Trend)
            {
                if (point.Value is not { } value) continue;
                if (point.BreakBefore || metric.DisplayTrend is null && previousEnd.HasValue && point.StartSeconds > previousEnd.Value + 1e-9)
                    line.Points.Add(DataPoint.Undefined);
                var x = calendar ? DateTimeAxis.ToDouble(row.Bundle.StartUtc!.Value.AddSeconds(point.StartSeconds).UtcDateTime)
                    : (point.StartSeconds - row.Bundle.StartSeconds) / 60;
                line.Points.Add(new(x, value)); previousEnd = point.EndSeconds;
            }
            model.Series.Add(line);
        }
        if (model.Series.Count == 0) model.Subtitle = "Deze uitkomst bevat geen minuuttrend; de tabel blijft beschikbaar.";
        Trend = model;
    }
    private static string Format(double? value) => value is { } number && double.IsFinite(number)
        ? number.ToString("0.######", CultureInfo.GetCultureInfo("nl-NL")) : "Niet beschikbaar";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loading?.Cancel(); // The active operation owns disposal of its cancellation source and file reader.
        foreach (var row in Bundles) row.PropertyChanged -= OnBundleSelectionChanged;
        Bundles.Clear(); Results.Clear(); Metrics.Clear(); Machines.Clear(); SelectedMetric = null;
        Trend = new(); AggregateValue = "Niet beschikbaar"; AggregateExplanation = "Vergelijking gesloten.";
        Status = "Vergelijking gesloten.";
        OpenBundlesCommand.NotifyCanExecuteChanged(); ClearBundlesCommand.NotifyCanExecuteChanged(); CancelLoadingCommand.NotifyCanExecuteChanged();
    }
}
