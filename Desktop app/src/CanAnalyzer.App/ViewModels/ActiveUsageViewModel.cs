using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class ActiveUsageViewModel : ObservableObject
{
    private const int MaximumAnalysisPointsPerSignal = 250_000;
    private const string NoSignal = "(geen — gebruik meetdekking als schatting)";
    private readonly ITelemetryService _telemetryService;
    private readonly Dictionary<string, SignalSeries> _analysisSeriesCache = new(StringComparer.Ordinal);
    private CanDataset? _dataset;
    private CancellationTokenSource? _calculation;
    private int _revision;
    private double? _start;
    private double? _end;
    private bool _loading;

    [ObservableProperty] private string? _activitySignal;
    [ObservableProperty] private string? _socSignal;
    [ObservableProperty] private string? _onSignal = NoSignal;
    [ObservableProperty] private int _directionIndex;
    [ObservableProperty] private bool _useSocSlope;
    [ObservableProperty] private string _threshold = "100";
    [ObservableProperty] private string _onThreshold = "0,5";
    [ObservableProperty] private string _bridgeSeconds = "10";
    [ObservableProperty] private string _minimumSeconds = "3";
    [ObservableProperty] private string _maxGapSeconds = "5";
    [ObservableProperty] private string _socWindow = "60";
    [ObservableProperty] private string _socRateThreshold = "5";
    [ObservableProperty] private string _capacityKwh = "15";
    [ObservableProperty] private string _reserveSoc = "20";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _status = "Laad meetdata om actief gebruik en batterijduur te berekenen.";
    [ObservableProperty] private string _scope = "Hele meting; de tijdvelden bovenaan begrenzen de analyse.";
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private PlotModel _timeline = new();

    public ActiveUsageViewModel(ITelemetryService telemetryService)
    {
        _telemetryService = telemetryService;
        CalculateCommand = new AsyncRelayCommand(CalculateAsync, () => _dataset is not null && !IsBusy);
    }

    public ObservableCollection<string> Signals { get; } = [];
    public ObservableCollection<string> OnSignals { get; } = [NoSignal];
    public string[] Directions { get; } = ["Beide richtingen: |waarde|", "Positieve waarde (bijv. ontladen +)", "Negatieve waarde (bijv. ontladen −)"];
    public ObservableCollection<MetricRow> TimeMetrics { get; } = [];
    public ObservableCollection<MetricRow> EnergyMetrics { get; } = [];
    public ObservableCollection<MetricRow> BatteryMetrics { get; } = [];
    public IAsyncRelayCommand CalculateCommand { get; }

    partial void OnIsBusyChanged(bool value) => CalculateCommand.NotifyCanExecuteChanged();

    partial void OnActivitySignalChanged(string? value)
    {
        if (_loading || value is null) return;
        Threshold = IsBmsCurrent(value) ? "5" : Contains(value, "Force") ? "1000" : IsMotorSpeed(value) ? "100" : Threshold;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is nameof(ActivitySignal) or nameof(SocSignal) or nameof(OnSignal) or
            nameof(UseSocSlope) or nameof(Threshold) or nameof(OnThreshold) or nameof(BridgeSeconds) or
            nameof(MinimumSeconds) or nameof(MaxGapSeconds) or nameof(SocWindow) or nameof(SocRateThreshold) or
            nameof(CapacityKwh) or nameof(ReserveSoc) or nameof(DirectionIndex))
            Invalidate();
    }

    public void LoadDataset(CanDataset dataset)
    {
        Invalidate();
        _dataset = dataset;
        _analysisSeriesCache.Clear();
        _loading = true;
        try
        {
            Signals.Clear();
            OnSignals.Clear();
            OnSignals.Add(NoSignal);
            foreach (var label in dataset.SignalLabels) { Signals.Add(label); OnSignals.Add(label); }
            SocSignal = Signals.FirstOrDefault(x => Contains(x, "BMS_STATUS_SOC"))
                ?? Signals.FirstOrDefault(x => Contains(x, "SOC") && !Contains(x, "ERROR") && !Contains(x, "Limit"));
            ActivitySignal = Signals.FirstOrDefault(IsMotorSpeed)
                ?? Signals.FirstOrDefault(IsBmsCurrent)
                ?? Signals.FirstOrDefault(x => Contains(x, "ActuatorLeft.ActualForce"))
                ?? Signals.FirstOrDefault(x => Contains(x, "ActualForce"));
            Threshold = ActivitySignal is not null && IsBmsCurrent(ActivitySignal) ? "5" :
                ActivitySignal is not null && Contains(ActivitySignal, "Force") ? "1000" : "100";
            DirectionIndex = 0;
            // An enabled motor is not necessarily the machine's ignition/power state. Let the user choose.
            OnSignal = NoSignal;
            UseSocSlope = ActivitySignal is null && SocSignal is not null;
        }
        finally { _loading = false; }
        CalculateCommand.NotifyCanExecuteChanged();
        Status = "Signalen voorgeselecteerd. Controleer eenheden en grenswaarden; klik Bereken actief gebruik.";
    }

    public void SetTimeWindow(double? start, double? end)
    {
        _start = start;
        _end = end;
        Scope = $"Periode: {(start.HasValue ? Number(start.Value) + " s" : "begin meting")} → " +
            $"{(end.HasValue ? Number(end.Value) + " s" : "einde meting")}. Volgt de tijdvelden bovenaan; zoom en frame-ID-filter wijzigen deze analyse niet.";
        Invalidate();
    }

    private void Invalidate()
    {
        _revision++;
        _calculation?.Cancel();
        HasResult = false;
        TimeMetrics.Clear();
        EnergyMetrics.Clear();
        BatteryMetrics.Clear();
        Notes = string.Empty;
        Status = "Instellingen gewijzigd. Klik Bereken actief gebruik voor actuele resultaten.";
    }

    private async Task CalculateAsync()
    {
        if (_dataset is null) return;
        var dataset = _dataset;
        var revision = _revision;
        using var cancellation = new CancellationTokenSource();
        _calculation = cancellation;
        IsBusy = true;
        HasResult = false;
        Status = "Actief gebruik geheugenveilig berekenen over de volledige meetperiode…";
        var operationId = _telemetryService.BeginCriticalOperation("active_usage_analysis", new Dictionary<string, object?>
        {
            ["decoded_sample_bucket"] = TelemetryBuckets.Count(dataset.DecodedSamples.Count),
            ["maximum_points_per_signal"] = MaximumAnalysisPointsPerSignal
        });
        try
        {
            var activity = Resolve(dataset, ActivitySignal);
            var soc = Resolve(dataset, SocSignal);
            var on = Resolve(dataset, OnSignal);
            var options = new ActiveUsageOptions
            {
                Method = UseSocSlope ? ActivityDetectionMethod.SocSlope : ActivityDetectionMethod.SignalThreshold,
                Direction = (ActivitySignalDirection)DirectionIndex,
                ActivityThreshold = Parse(Threshold, "Activiteitsgrens"), OnThreshold = Parse(OnThreshold, "Aan-grens"),
                BridgePauseSeconds = Parse(BridgeSeconds, "Korte pauze"), MinimumActivitySeconds = Parse(MinimumSeconds, "Minimum werkperiode"),
                MaximumSampleGapSeconds = Parse(MaxGapSeconds, "Maximale sampleafstand"),
                SocWindowSeconds = Parse(SocWindow, "SOC-venster"), SocDropPerHourThreshold = Parse(SocRateThreshold, "SOC-daling"),
                BatteryCapacityKwh = Parse(CapacityKwh, "Accucapaciteit"), ReserveSocPercent = Parse(ReserveSoc, "Reserve"),
                StartSeconds = _start ?? (dataset.RawCount > 0 ? dataset.RawFrames[0].TimeSeconds : null),
                EndSeconds = _end ?? (dataset.RawCount > 0 ? dataset.RawFrames[^1].TimeSeconds : null)
            };
            var result = await Task.Run(() => new ActiveUsageAnalyzer().Analyze(activity, soc, on, options,
                dataset.ImportReport?.Gaps, cancellation.Token), cancellation.Token);
            if (revision != _revision) return;
            ShowResult(result, dataset.Completeness);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (revision == _revision) Status = $"Actief gebruik niet berekend: {ex.Message}";
        }
        finally
        {
            _telemetryService.CompleteCriticalOperation(operationId);
            if (ReferenceEquals(_calculation, cancellation)) _calculation = null;
            IsBusy = false;
        }
    }

    private void ShowResult(ActiveUsageResult r, DatasetCompleteness completeness)
    {
        TimeMetrics.Clear();
        EnergyMetrics.Clear();
        BatteryMetrics.Clear();
        Add(TimeMetrics, "Totale periode", Duration(r.EndSeconds - r.StartSeconds));
        Add(TimeMetrics, r.HasOnSignal ? "Gemeten aan-tijd" : "Aan-tijd (schatting meetdekking)", Duration(r.OnSeconds));
        Add(TimeMetrics, "Actief gebruik", Duration(r.ActiveSeconds));
        Add(TimeMetrics, "Aandeel actief / aan", (r.Duration(UsageState.UnknownActivity) > 0 ? "minstens " : "") + Format(r.ActivePercent, "%"));
        Add(TimeMetrics, "Aan, zonder activiteit", Duration(r.IdleSeconds));
        Add(TimeMetrics, "Uit (aan/uit-signaal)", r.HasOnSignal ? Duration(r.Duration(UsageState.Off)) : "niet vastgesteld");
        Add(TimeMetrics, "Aan, activiteit onbekend", Duration(r.Duration(UsageState.UnknownActivity)));
        Add(TimeMetrics, "Geen dekking / aan-status onbekend", Duration(r.Duration(UsageState.Unknown)));

        var total = r.TotalEnergy;
        var active = r.EnergyByState[UsageState.Active];
        var idle = r.EnergyByState[UsageState.Idle];
        Add(EnergyMetrics, "SOC eerste → laatste geldige meting", r.StartSoc.HasValue ? $"{Number(r.StartSoc.Value)} → {Number(r.EndSoc!.Value)} %" : "niet beschikbaar");
        Add(EnergyMetrics, "Netto SOC begin → einde (incl. gaten)", Format(r.StartSoc - r.EndSoc, "procentpunt"));
        Add(EnergyMetrics, "Tijd met geldige SOC-dekking", Duration(total.CoveredSeconds));
        Add(EnergyMetrics, "Netto energie binnen SOC-dekking", total.CoveredSeconds > 0 ? Format(total.NetKwh, "kWh") : "niet beschikbaar");
        Add(EnergyMetrics, "Actief: SOC / energie", EnergyText(active));
        Add(EnergyMetrics, "Aan zonder activiteit: SOC / energie", EnergyText(idle));
        Add(EnergyMetrics, "Uit / onbekend: netto energie", total.CoveredSeconds > 0 ? Format(total.NetKwh - active.NetKwh - idle.NetKwh, "kWh") : "niet beschikbaar");
        Add(EnergyMetrics, "SOC-stijging (laden / BMS-correctie)", total.CoveredSeconds > 0 ? Format(total.RiseSocPoints, "procentpunt") : "niet beschikbaar");

        Add(BatteryMetrics, "Gemiddeld vermogen tijdens activiteit", Format(active.AverageKw, "kW"));
        Add(BatteryMetrics, "Gemiddeld vermogen aan zonder activiteit", Format(idle.AverageKw, "kW"));
        Add(BatteryMetrics, "Volle accu: continu actief (100 → 0%)", Hours(r.ActiveRuntimeHours));
        Add(BatteryMetrics, "Volle accu: huidig gebruikspatroon", Hours(r.MixedRuntimeHours));
        Add(BatteryMetrics, $"Huidig patroon: 100 → {Number(r.Options.ReserveSocPercent)}%", Hours(r.MixedRuntimeHours * (100 - r.Options.ReserveSocPercent) / 100));
        Add(BatteryMetrics, $"Resterend: continu actief tot {Number(r.Options.ReserveSocPercent)}%", Hours(r.RemainingActiveHours));
        Add(BatteryMetrics, $"Resterend: huidig patroon tot {Number(r.Options.ReserveSocPercent)}%", Hours(r.RemainingMixedHours));
        Add(BatteryMetrics, "SOC-dekking actief / inactief", $"{Coverage(active.CoveredSeconds, r.ActiveSeconds)} / {Coverage(idle.CoveredSeconds, r.IdleSeconds)}");

        var notes = new List<string>
        {
            r.HasOnSignal ? "Aan-tijd volgt het gekozen aan/uit-signaal." : "Zonder aan/uit-signaal is geldige meetdekking een schatting van aan-tijd; ontbrekende data bewijzen geen stilstand.",
            r.Options.Method == ActivityDetectionMethod.SocSlope
                ? $"Activiteit afgeleid uit SOC-daling ≥ {Number(r.Options.SocDropPerHourThreshold)} procentpunt/uur in vensters van {Number(r.Options.SocWindowSeconds)} s. Controleer dit bij voorkeur met RPM of actuatorkracht."
                : $"Activiteit: {(r.Options.Direction == ActivitySignalDirection.Absolute ? "|signaal|" : r.Options.Direction == ActivitySignalDirection.Negative ? "−signaal" : "signaal")} > {Number(r.Options.ActivityThreshold)} in de gedecodeerde eenheid. Bij BMS-stroom: kies het teken van ontladen om laadstroom uit te sluiten.",
            $"Korte bekende pauzes tot {Number(r.Options.BridgePauseSeconds)} s tellen mee binnen een werkperiode; minimumduur {Number(r.Options.MinimumActivitySeconds)} s. Datagaten worden nooit overbrugd.",
            $"Energie is geschat uit SOC met {Number(r.Options.BatteryCapacityKwh)} kWh bruikbare capaciteit en een lineaire SOC-schaal. Vermogen gebruikt uitsluitend de bijbehorende tijd met SOC-dekking (1 kW = 1 kWh per uur).",
            "Batterijduur is een extrapolatie bij dezelfde belasting en verdeling actief/inactief. Minimaal 90% SOC-dekking is vereist per gebruikte toestand; een onbekende gebruiksverdeling krijgt geen voorspelling."
        };
        if (!r.CanProject) notes.Add("SOC stijgt meer dan 0,5 procentpunt: batterijduur is niet voorspeld wegens mogelijk laden of BMS-correcties.");
        if (completeness == DatasetCompleteness.Partial) notes.Add("PARTIAL-dataset: afgewezen of ontbrekende brondata kunnen de uitkomst beïnvloeden.");
        Notes = string.Join("\n", notes);
        Timeline = BuildTimeline(r);
        Status = $"Berekend over {Number(r.StartSeconds)}–{Number(r.EndSeconds)} s. {r.Intervals.Count(x => x.State == UsageState.Active)} werkperiodes gevonden.";
        HasResult = true;
    }

    private static PlotModel BuildTimeline(ActiveUsageResult r)
    {
        var model = new PlotModel { Title = "Gebruiksperiodes", TitleFontSize = 13, IsLegendVisible = true };
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Tijd [s]", Minimum = r.StartSeconds, Maximum = r.EndSeconds });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = -0.5, Maximum = 3.5,
            MajorStep = 1, IsZoomEnabled = false, IsPanEnabled = false,
            LabelFormatter = value => value switch { 3 => "Actief", 2 => "Inactief", 1 => "Uit", 0 => "Onbekend", _ => "" } });
        foreach (var state in Enum.GetValues<UsageState>())
        {
            var (name, level, color) = state switch
            {
                UsageState.Active => ("Actief", 3, OxyColor.Parse("#16834A")),
                UsageState.Idle => ("Aan, inactief", 2, OxyColor.Parse("#3775B0")),
                UsageState.Off => ("Uit", 1, OxyColors.Gray),
                UsageState.UnknownActivity => ("Aan, activiteit onbekend", 0, OxyColor.Parse("#A65D00")),
                _ => ("Geen dekking", 0, OxyColor.Parse("#C28E31"))
            };
            var series = new LineSeries { Title = name, Color = color, StrokeThickness = 6 };
            foreach (var interval in r.Intervals.Where(x => x.State == state))
            {
                series.Points.Add(new DataPoint(interval.StartSeconds, level));
                series.Points.Add(new DataPoint(interval.EndSeconds, level));
                series.Points.Add(DataPoint.Undefined);
            }
            if (series.Points.Count > 0) model.Series.Add(series);
        }
        return model;
    }

    private SignalSeries? Resolve(CanDataset dataset, string? label)
    {
        if (label is null || !dataset.SignalSeriesByLabel.TryGetValue(label, out var series)) return null;
        if (_analysisSeriesCache.TryGetValue(label, out var cached)) return cached;
        cached = series.ForAnalysis(MaximumAnalysisPointsPerSignal);
        _analysisSeriesCache[label] = cached;
        return cached;
    }
    private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
    private static bool IsBmsCurrent(string label) => (Contains(label, "BMS") || Contains(label, "Battery") || Contains(label, "Pack")) &&
        Contains(label, "Current") && !Contains(label, "Limit") && !Contains(label, "Max") && !Contains(label, "Allowed") && !Contains(label, "Request");
    private static bool IsMotorSpeed(string label) => !Contains(label, "Target") && !Contains(label, "Command") && !Contains(label, "Limit") &&
        (Contains(label, "RPM") || Contains(label, "MotorSpeed") || Contains(label, "Motor_Speed") || Contains(label, "ActualVelocity") || Contains(label, "ActualSpeed"));
    private static double Parse(string text, string label) => double.TryParse(text.Replace(',', '.'), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new ArgumentException($"{label}: voer een geldig getal in.");
    private static void Add(ObservableCollection<MetricRow> rows, string label, string value) => rows.Add(new MetricRow(label, value));
    private static string Number(double value) => value.ToString("0.##", CultureInfo.GetCultureInfo("nl-NL"));
    private static string Format(double? value, string unit) => value.HasValue && double.IsFinite(value.Value) ? $"{Number(value.Value)} {unit}" : "niet beschikbaar";
    private static string Duration(double seconds) => $"{(int)(seconds / 3600)} u {(int)(seconds % 3600 / 60):00} min {seconds % 60:00} s";
    private static string Hours(double? hours) => hours.HasValue && double.IsFinite(hours.Value) && hours.Value <= 100_000
        ? $"≈ {Number(hours.Value)} uur" : "niet betrouwbaar te bepalen";
    private static string Coverage(double covered, double total) => total > 0 ? Format(100 * covered / total, "%") : "n.v.t.";
    private static string EnergyText(UsageEnergy energy) => energy.CoveredSeconds > 0
        ? $"{Number(energy.NetSocPoints)} procentpunt / {Number(energy.NetKwh)} kWh" : "niet beschikbaar";
}
