using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.Windows.Threading;

namespace CanAnalyzer.App.ViewModels;

public sealed partial class JoystickAnalyticsViewModel : ObservableObject
{
    private const int MaximumAnalysisPointsPerSignal = 250_000;
    private readonly ITelemetryService _telemetryService;
    private readonly Dictionary<string, SignalSeries> _analysisSeriesCache = new(StringComparer.Ordinal);
    private bool _analysisWasSampled;
    private int _largestAnalysisSource;
    private readonly IJoystickAnalyticsService _analyticsService;
    private readonly DispatcherTimer _joystickPlaybackTimer;
    private CanDataset? _dataset;
    private DelayAnalysisResult? _lastDelayResult;
    private bool _suppressTimeWindowAutoRecompute;
    private bool _suppressPlaybackAutoRefresh;
    private DateTime _lastPlaybackTickUtc;
    private IReadOnlyList<JoystickUsagePoint> _currentJoystickFilteredPath = [];
    private double _currentJoystickDeadzone;
    private double _currentJoystickSaturation;


    [ObservableProperty] private string _joystickUsageStatus = "Laad een log om joystickgebruik en actuatorbereik te bekijken.";
    [ObservableProperty] private string _joystickDeadzoneText = "—";
    [ObservableProperty] private string _joystickSaturationText = "—";
    [ObservableProperty] private string _joystickCoverageText = "—";
    [ObservableProperty] private double _strokeEdgePercent = 5;
    public ObservableCollection<StrokeUsageCard> StrokeCards { get; } = [];
    [ObservableProperty] private PlotModel _strokeDistributionModel = EmptyPlot("Geen actuatorposities geselecteerd");
    [ObservableProperty] private string? _selectedJoystickXSignal;
    [ObservableProperty] private string? _selectedJoystickYSignal;
    [ObservableProperty] private string? _selectedJoystickYawSignal;
    [ObservableProperty] private string? _selectedActuatorLeftSignal;
    [ObservableProperty] private string? _selectedActuatorRightSignal;
    [ObservableProperty] private string? _selectedActuatorFrontSignal;
    [ObservableProperty] private string? _selectedCommandSignal;
    [ObservableProperty] private string? _selectedResponseSignal;
    [ObservableProperty] private int _histogramBins = 40;
    [ObservableProperty] private double _deadzoneThreshold = 0.10;
    [ObservableProperty] private double _saturationThreshold = 0.90;
    [ObservableProperty] private bool _useJoystickTimeWindow;
    [ObservableProperty] private double _joystickTimeStartSeconds;
    [ObservableProperty] private double _joystickTimeEndSeconds;
    [ObservableProperty] private double _joystickAvailableStartSeconds;
    [ObservableProperty] private double _joystickAvailableEndSeconds;
    [ObservableProperty] private bool _isJoystickPlaybackEnabled;
    [ObservableProperty] private bool _isJoystickPlaybackRunning;
    [ObservableProperty] private double _joystickPlaybackStartSeconds;
    [ObservableProperty] private double _joystickPlaybackEndSeconds;
    [ObservableProperty] private double _joystickPlaybackPositionSeconds;
    [ObservableProperty] private double _joystickPlaybackSpeed = 1.0;
    [ObservableProperty] private bool _delayOverlayNormalize = true;
    [ObservableProperty] private bool _delayOverlayStepPlot = true;
    [ObservableProperty] private bool _delayOverlaySampleMarkers;
    [ObservableProperty] private bool _delayOverlayShowLegend = true;
    [ObservableProperty] private bool _delayOverlayShowDelayMarkers = true;
    [ObservableProperty] private double _canBitrateKbps = 500;
    [ObservableProperty] private double _canDataBitrateKbps = 2000;
    [ObservableProperty] private int _canTimeBinMilliseconds = 100;
    [ObservableProperty] private double _delaySearchRangeSeconds = 1.5;
    [ObservableProperty] private double _responseThresholdPercent = 2.0;
    [ObservableProperty] private string _statusText = "Laad eerst log + DBC en open daarna dit tabblad.";
    [ObservableProperty] private string _actuatorSummary = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyLabel = "Analyses herberekenen...";

    [ObservableProperty] private PlotModel _joystickDensityModel = EmptyPlot("Joystick puntenwolk");
    [ObservableProperty] private PlotModel _trajectoryModel = EmptyPlot("Joystick traject");
    [ObservableProperty] private PlotModel _radiusHistogramModel = EmptyPlot("Joystick radius histogram");
    [ObservableProperty] private PlotModel _actuatorLeftOverlayModel = EmptyPlot("Left actuator");
    [ObservableProperty] private PlotModel _actuatorRightOverlayModel = EmptyPlot("Right actuator");
    [ObservableProperty] private PlotModel _actuatorFrontOverlayModel = EmptyPlot("Front actuator");
    [ObservableProperty] private PlotModel _actuatorDelayHistogramModel = EmptyPlot("Delay histogram vlinder");
    [ObservableProperty] private PlotModel _delayHistogramModel = EmptyPlot("Delay histogram");
    [ObservableProperty] private PlotModel _delayOverlayModel = EmptyPlot("Command/response overlay");
    [ObservableProperty] private PlotModel _canFrameRateModel = EmptyPlot("Frames/s over tijd");
    [ObservableProperty] private PlotModel _canBusLoadModel = EmptyPlot("Bus load [%] over tijd");
    [ObservableProperty] private PlotModel _canTopIdPlotModel = EmptyPlot("Top CAN-IDs");
    [ObservableProperty] private IPlotController _delayOverlayController = CreateInteractiveController();

    public JoystickAnalyticsViewModel(IJoystickAnalyticsService analyticsService, ITelemetryService telemetryService, ActiveUsageViewModel activeUsage)
    {
        _analyticsService = analyticsService;
        _telemetryService = telemetryService;
        ActiveUsage = activeUsage;
        RecomputeCommand = new AsyncRelayCommand(RecomputeAsync, () => !IsBusy);
        RecomputeUsageCommand = new AsyncRelayCommand(RecomputeUsageAsync, () => !IsBusy);
        AutoDetectJoystickPairCommand = new RelayCommand(AutoDetectJoystickSignals);
        AutoDetectActuatorPairCommand = new RelayCommand(AutoDetectButterflySignals);
        AutoDetectDelaySignalsCommand = new RelayCommand(AutoDetectDelaySignals);
        ResetJoystickTimeWindowCommand = new RelayCommand(ResetJoystickTimeWindow);
        ToggleJoystickPlaybackCommand = new RelayCommand(ToggleJoystickPlayback);
        ResetJoystickPlaybackCommand = new RelayCommand(ResetJoystickPlayback);
        _joystickPlaybackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _joystickPlaybackTimer.Tick += JoystickPlaybackTimerOnTick;
    }

    public ActiveUsageViewModel ActiveUsage { get; }
    public ObservableCollection<string> AvailableSignals { get; } = [];
    public ObservableCollection<MetricRow> JoystickUsageMetrics { get; } = [];
    public ObservableCollection<ActuatorMetricRow> ActuatorMatrix { get; } = [];
    public ObservableCollection<MetricRow> DelayMetrics { get; } = [];
    public ObservableCollection<MetricRow> ProfessionalCanMetrics { get; } = [];
    public ObservableCollection<CanTopIdRow> ProfessionalTopIdRows { get; } = [];
    public ObservableCollection<CanCycleTimingRow> ProfessionalCycleRows { get; } = [];
    public IAsyncRelayCommand RecomputeCommand { get; }
    public IAsyncRelayCommand RecomputeUsageCommand { get; }
    public IRelayCommand AutoDetectJoystickPairCommand { get; }
    public IRelayCommand AutoDetectActuatorPairCommand { get; }
    public IRelayCommand AutoDetectDelaySignalsCommand { get; }
    public IRelayCommand ResetJoystickTimeWindowCommand { get; }
    public IRelayCommand ToggleJoystickPlaybackCommand { get; }
    public IRelayCommand ResetJoystickPlaybackCommand { get; }

    private void InvalidateUsage()
    {
        if (IsBusy) return;
        StopJoystickPlayback();
        _currentJoystickFilteredPath = [];
        StrokeCards.Clear();
        JoystickUsageMetrics.Clear();
        JoystickDeadzoneText = JoystickSaturationText = JoystickCoverageText = "—";
        JoystickUsageStatus = "Instellingen gewijzigd. Klik op Bereken analyse om de resultaten bij te werken.";
        JoystickDensityModel = EmptyPlot("Bereken analyse");
        RadiusHistogramModel = EmptyPlot("Bereken analyse");
        TrajectoryModel = EmptyPlot("Bereken analyse");
        StrokeDistributionModel = EmptyPlot("Bereken analyse");
    }
    partial void OnSelectedJoystickXSignalChanged(string? value) => InvalidateUsage();
    partial void OnSelectedJoystickYSignalChanged(string? value) => InvalidateUsage();
    partial void OnSelectedActuatorLeftSignalChanged(string? value) => InvalidateUsage();
    partial void OnSelectedActuatorRightSignalChanged(string? value) => InvalidateUsage();
    partial void OnSelectedActuatorFrontSignalChanged(string? value) => InvalidateUsage();
    partial void OnHistogramBinsChanged(int value) => InvalidateUsage();
    partial void OnDeadzoneThresholdChanged(double value) => InvalidateUsage();
    partial void OnSaturationThresholdChanged(double value) => InvalidateUsage();
    partial void OnStrokeEdgePercentChanged(double value) => InvalidateUsage();

    partial void OnUseJoystickTimeWindowChanged(bool value) => RecomputeJoystickWindowIfNeeded();
    partial void OnJoystickTimeStartSecondsChanged(double value) => RecomputeJoystickWindowIfNeeded();
    partial void OnJoystickTimeEndSecondsChanged(double value) => RecomputeJoystickWindowIfNeeded();
    partial void OnIsJoystickPlaybackEnabledChanged(bool value)
    {
        if (!value)
        {
            StopJoystickPlayback();
        }

        UpdateJoystickPlaybackPlots();
    }

    partial void OnJoystickPlaybackPositionSecondsChanged(double value) => UpdateJoystickPlaybackPlots();
    partial void OnJoystickPlaybackSpeedChanged(double value)
    {
        if (!double.IsFinite(value) || value < 0.1)
        {
            JoystickPlaybackSpeed = 0.1;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        RecomputeCommand.NotifyCanExecuteChanged();
        RecomputeUsageCommand.NotifyCanExecuteChanged();
    }

    partial void OnResponseThresholdPercentChanged(double value)
    {
        if (value < 0.1)
        {
            ResponseThresholdPercent = 0.1;
            return;
        }

        if (value > 50)
        {
            ResponseThresholdPercent = 50;
            return;
        }

        if (_dataset is not null)
        {
            BuildDelayAnalytics();
        }
    }

    partial void OnDelayOverlayNormalizeChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnDelayOverlayStepPlotChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnDelayOverlaySampleMarkersChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnDelayOverlayShowLegendChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnDelayOverlayShowDelayMarkersChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnCanBitrateKbpsChanged(double value)
    {
        if (value < 50)
        {
            CanBitrateKbps = 50;
            return;
        }

        RefreshProfessionalCanAnalyticsFromSettings();
    }

    partial void OnCanDataBitrateKbpsChanged(double value)
    {
        if (value < 50)
        {
            CanDataBitrateKbps = 50;
            return;
        }

        RefreshProfessionalCanAnalyticsFromSettings();
    }

    partial void OnCanTimeBinMillisecondsChanged(int value)
    {
        if (value < 50)
        {
            CanTimeBinMilliseconds = 50;
            return;
        }

        if (value > 5000)
        {
            CanTimeBinMilliseconds = 5000;
            return;
        }

        RefreshProfessionalCanAnalyticsFromSettings();
    }

    public void LoadDataset(CanDataset dataset)
    {
        StopJoystickPlayback();
        _suppressTimeWindowAutoRecompute = true;
        try
        {
            UseJoystickTimeWindow = false;
            JoystickTimeStartSeconds = JoystickTimeEndSeconds = 0;
        }
        finally { _suppressTimeWindowAutoRecompute = false; }
        _dataset = dataset;
        ActiveUsage.LoadDataset(dataset);
        _analysisSeriesCache.Clear();
        AvailableSignals.Clear();
        foreach (var label in dataset.SignalLabels) AvailableSignals.Add(label);
        AutoDetectButterflySignals();
        AutoDetectDelaySignals();
        _ = RecomputeAsync();
    }

    private async Task RecomputeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_dataset is null || _dataset.SignalSeriesByLabel.Count == 0)
        {
            ClearOutputs();
            StatusText = "Geen dataset met signalen beschikbaar.";
            return;
        }

        IsBusy = true;
        _analysisWasSampled = false;
        _largestAnalysisSource = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var operationProperties = new Dictionary<string, object?>
        {
            ["decoded_sample_bucket"] = TelemetryBuckets.Count(_dataset.DecodedSamples.Count),
            ["signal_bucket"] = TelemetryBuckets.Count(_dataset.SignalCount),
            ["maximum_points_per_signal"] = MaximumAnalysisPointsPerSignal
        };
        var operationId = _telemetryService.BeginCriticalOperation("analytics_recompute", operationProperties);
        try
        {
            _ = _telemetryService.TrackEventAsync("analytics_recompute_started", operationProperties);
            BusyLabel = "Joystickanalyse herberekenen...";
            await Dispatcher.Yield(DispatcherPriority.Background);
            BuildJoystickUsageAnalytics();

            BusyLabel = "Actuatoranalyse herberekenen...";
            await Dispatcher.Yield(DispatcherPriority.Background);
            BuildActuatorTrackingAnalytics();

            BusyLabel = "Vertragingsanalyse herberekenen...";
            await Dispatcher.Yield(DispatcherPriority.Background);
            BuildDelayAnalytics();

            BusyLabel = "CAN-overzichten herberekenen...";
            await Dispatcher.Yield(DispatcherPriority.Background);
            BuildProfessionalCanAnalytics();
            var boundedText = _analysisWasSampled
                ? $" Alleen voor delay/kinematica zijn per signaal maximaal {MaximumAnalysisPointsPerSignal:N0} gelijkmatig verdeelde punten uit de volledige meetperiode gebruikt (grootste bron: {_largestAnalysisSource:N0})."
                : string.Empty;
            StatusText = (_dataset.Completeness == DatasetCompleteness.Partial
                ? "PARTIAL — analyses zijn gebaseerd op bewust onvolledig geaccepteerde data."
                : "COMPLETE — analyses bijgewerkt.") + boundedText;
            _ = _telemetryService.TrackEventAsync("analytics_recompute_completed", new Dictionary<string, object?>
            {
                ["duration_ms"] = stopwatch.ElapsedMilliseconds,
                ["input_sampled"] = _analysisWasSampled,
                ["largest_source_sample_bucket"] = TelemetryBuckets.Count(_largestAnalysisSource),
                ["maximum_points_per_signal"] = MaximumAnalysisPointsPerSignal
            });
        }
        catch (Exception ex)
        {
            StatusText = "Analyse kon niet worden voltooid; de fout is vastgelegd in telemetry. De geladen brondata blijft beschikbaar.";
            _ = _telemetryService.TrackEventAsync("analytics_recompute_failed", new Dictionary<string, object?>
            {
                ["duration_ms"] = stopwatch.ElapsedMilliseconds,
                ["exception_type"] = ex.GetType().Name,
                ["input_sampled"] = _analysisWasSampled,
                ["largest_source_sample_bucket"] = TelemetryBuckets.Count(_largestAnalysisSource)
            });
        }
        finally
        {
            stopwatch.Stop();
            _telemetryService.CompleteCriticalOperation(operationId);
            IsBusy = false;
        }
    }

    private void RefreshProfessionalCanAnalyticsFromSettings()
    {
        if (_dataset is null)
        {
            return;
        }

        BuildProfessionalCanAnalytics();
    }

    private void RecomputeJoystickWindowIfNeeded()
    {
        if (_suppressTimeWindowAutoRecompute || _dataset is null) return;
        InvalidateUsage();
    }

    private void UpdateJoystickPlaybackPlots()
    {
        if (_suppressPlaybackAutoRefresh) return;
        if (_currentJoystickFilteredPath.Count == 0)
        {
            TrajectoryModel = EmptyPlot("Geen traject beschikbaar");
            return;
        }
        var end = IsJoystickPlaybackEnabled ? JoystickPlaybackPositionSeconds : JoystickPlaybackEndSeconds;
        int UpperPathBound(double time)
        {
            var low = 0;
            var high = _currentJoystickFilteredPath.Count;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (_currentJoystickFilteredPath[mid].Time <= time) low = mid + 1;
                else high = mid;
            }
            return low;
        }
        var first = Math.Max(0, UpperPathBound(end - 5) - 1);
        var last = UpperPathBound(end);
        var visible = new List<JoystickUsagePoint>();
        for (var i = first; i < last; i++)
            if (_currentJoystickFilteredPath[i].Time + _currentJoystickFilteredPath[i].Seconds >= end - 5)
                visible.Add(_currentJoystickFilteredPath[i]);
        if (visible.Count == 0 || visible[^1].Time + visible[^1].Seconds < end - 0.000001)
        {
            TrajectoryModel = EmptyPlot("Geen meetdekking op dit tijdstip");
            return;
        }
        var points = new List<NormalizedPoint>();
        for (var i = 0; i < visible.Count; i++)
        {
            if (i > 0 && visible[i].Time > visible[i - 1].Time + visible[i - 1].Seconds + 0.000001)
                points.Add(new NormalizedPoint(double.NaN, double.NaN));
            points.Add(new NormalizedPoint(visible[i].X, visible[i].Y));
        }
        TrajectoryModel = BuildTrajectory(points, _currentJoystickDeadzone, _currentJoystickSaturation, points[^1]);
    }

    private void ToggleJoystickPlayback()
    {
        if (_currentJoystickFilteredPath.Count == 0)
        {
            return;
        }

        if (IsJoystickPlaybackRunning)
        {
            StopJoystickPlayback();
            return;
        }

        if (!IsJoystickPlaybackEnabled)
        {
            IsJoystickPlaybackEnabled = true;
        }

        if (JoystickPlaybackPositionSeconds >= JoystickPlaybackEndSeconds - 1e-9)
        {
            _suppressPlaybackAutoRefresh = true;
            try
            {
                JoystickPlaybackPositionSeconds = JoystickPlaybackStartSeconds;
            }
            finally
            {
                _suppressPlaybackAutoRefresh = false;
            }

            UpdateJoystickPlaybackPlots();
        }

        _lastPlaybackTickUtc = DateTime.UtcNow;
        _joystickPlaybackTimer.Start();
        IsJoystickPlaybackRunning = true;
    }

    private void ResetJoystickPlayback()
    {
        StopJoystickPlayback();
        if (_currentJoystickFilteredPath.Count == 0)
        {
            return;
        }

        _suppressPlaybackAutoRefresh = true;
        try
        {
            JoystickPlaybackPositionSeconds = JoystickPlaybackStartSeconds;
        }
        finally
        {
            _suppressPlaybackAutoRefresh = false;
        }

        UpdateJoystickPlaybackPlots();
    }

    private void StopJoystickPlayback()
    {
        if (_joystickPlaybackTimer.IsEnabled)
        {
            _joystickPlaybackTimer.Stop();
        }

        IsJoystickPlaybackRunning = false;
    }

    private void JoystickPlaybackTimerOnTick(object? sender, EventArgs e)
    {
        if (!IsJoystickPlaybackRunning || _currentJoystickFilteredPath.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = Math.Max(0.001, (now - _lastPlaybackTickUtc).TotalSeconds);
        _lastPlaybackTickUtc = now;
        var next = JoystickPlaybackPositionSeconds + (elapsed * Math.Max(0.1, JoystickPlaybackSpeed));
        if (next >= JoystickPlaybackEndSeconds)
        {
            next = JoystickPlaybackEndSeconds;
            StopJoystickPlayback();
        }

        _suppressPlaybackAutoRefresh = true;
        try
        {
            JoystickPlaybackPositionSeconds = next;
        }
        finally
        {
            _suppressPlaybackAutoRefresh = false;
        }

        UpdateJoystickPlaybackPlots();
    }

    private void RefreshDelayOverlayFromCache()
    {
        if (_lastDelayResult is null)
        {
            DelayOverlayModel = EmptyPlot("Command/response overlay");
            return;
        }

        DelayOverlayModel = BuildDelayOverlay(
            _lastDelayResult.CommandSeries,
            _lastDelayResult.ResponseSeries,
            _lastDelayResult.ShiftedResponseSeries,
            Math.Max(0.05, DelaySearchRangeSeconds),
            DelayOverlayNormalize,
            DelayOverlayStepPlot,
            DelayOverlaySampleMarkers,
            DelayOverlayShowLegend,
            DelayOverlayShowDelayMarkers,
            DelayOverlayShowDelayMarkers);
    }

    private void ClearOutputs()
    {
        StopJoystickPlayback();
        _lastDelayResult = null;
        StrokeCards.Clear();
        StrokeDistributionModel = EmptyPlot("Geen actuatorposities geselecteerd");
        JoystickDeadzoneText = JoystickSaturationText = JoystickCoverageText = "—";
        JoystickUsageStatus = "Laad een log om het gebruik te analyseren.";
        _currentJoystickFilteredPath = [];
        JoystickUsageMetrics.Clear();
        ActuatorMatrix.Clear();
        ActuatorSummary = string.Empty;
        DelayMetrics.Clear();
        ProfessionalCanMetrics.Clear();
        ProfessionalTopIdRows.Clear();
        ProfessionalCycleRows.Clear();
        JoystickDensityModel = EmptyPlot("Joystick puntenwolk");
        TrajectoryModel = EmptyPlot("Joystick traject");
        RadiusHistogramModel = EmptyPlot("Joystick radius histogram");
        ActuatorLeftOverlayModel = EmptyPlot("Left actuator");
        ActuatorRightOverlayModel = EmptyPlot("Right actuator");
        ActuatorFrontOverlayModel = EmptyPlot("Front actuator");
        ActuatorDelayHistogramModel = EmptyPlot("Delay histogram vlinder");
        DelayHistogramModel = EmptyPlot("Dode tijd histogram");
        DelayOverlayModel = EmptyPlot("Command/response overlay");
        CanFrameRateModel = EmptyPlot("Frames/s over tijd");
        CanBusLoadModel = EmptyPlot("Bus load [%] over tijd");
        CanTopIdPlotModel = EmptyPlot("Top CAN-IDs");
    }

    private async Task RecomputeUsageAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            JoystickUsageStatus = "Analyse wordt berekend…";
            await Dispatcher.Yield(DispatcherPriority.Background);
            BuildJoystickUsageAnalytics();
        }
        finally { IsBusy = false; }
    }

    private void BuildJoystickUsageAnalytics()
    {
        StopJoystickPlayback();
        _currentJoystickFilteredPath = [];
        JoystickUsageMetrics.Clear();
        JoystickDeadzoneText = JoystickSaturationText = JoystickCoverageText = "—";
        JoystickDensityModel = EmptyPlot("Geen gezamenlijke joystickdata");
        TrajectoryModel = EmptyPlot("Geen traject beschikbaar");
        RadiusHistogramModel = EmptyPlot("Geen uitslagverdeling beschikbaar");
        var selectedSeries = new[] { SelectedJoystickXSignal, SelectedJoystickYSignal, SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal }
            .Select(label => TryGetSeries(label, out var series) ? series : null)
            .Where(series => series is not null && series.Time.Length > 0).ToArray();
        _suppressTimeWindowAutoRecompute = true;
        try
        {
            JoystickAvailableStartSeconds = selectedSeries.Length > 0 ? selectedSeries.Min(s => s!.Time[0]) : 0;
            JoystickAvailableEndSeconds = selectedSeries.Length > 0 ? selectedSeries.Max(s => s!.Time[^1]) : 0;
            if (!UseJoystickTimeWindow)
            {
                JoystickTimeStartSeconds = JoystickAvailableStartSeconds;
                JoystickTimeEndSeconds = JoystickAvailableEndSeconds;
            }
        }
        finally { _suppressTimeWindowAutoRecompute = false; }
        if (!double.IsFinite(DeadzoneThreshold) || !double.IsFinite(SaturationThreshold)
            || DeadzoneThreshold < 0 || SaturationThreshold > 1 || DeadzoneThreshold >= SaturationThreshold
            || !double.IsFinite(StrokeEdgePercent) || StrokeEdgePercent <= 0 || StrokeEdgePercent >= 50
            || HistogramBins < 10 || HistogramBins > 100
            || (UseJoystickTimeWindow && (!double.IsFinite(JoystickTimeStartSeconds) || !double.IsFinite(JoystickTimeEndSeconds) || JoystickTimeEndSeconds <= JoystickTimeStartSeconds)))
        {
            StrokeCards.Clear();
            StrokeDistributionModel = EmptyPlot("Controleer de instellingen");
            JoystickUsageStatus = "Controleer de instellingen: 0 ≤ deadzone < buitenrand ≤ 1; uiterste band > 0 en < 50%; 10–100 bins; einde na start.";
            return;
        }
        BuildStrokeUsage();
        if (!TryGetSeries(SelectedJoystickXSignal, out var sx) || !TryGetSeries(SelectedJoystickYSignal, out var sy))
        {
            JoystickUsageStatus = "Selecteer joystick X en Y bij Signalen en instellingen. Slagbenutting werkt onafhankelijk van de joystick.";
            return;
        }
        if (sx.Time.Length == 0 || sy.Time.Length == 0)
        {
            JoystickUsageStatus = "De geselecteerde joysticksignalen bevatten geen meetpunten.";
            return;
        }
        if (UseJoystickTimeWindow && JoystickTimeEndSeconds <= JoystickTimeStartSeconds)
        {
            JoystickUsageStatus = "Ongeldig tijdvenster: einde moet na start liggen.";
            return;
        }
        var result = UsageAnalytics.AnalyzeJoystick(sx, sy,
            UseJoystickTimeWindow ? JoystickTimeStartSeconds : null,
            UseJoystickTimeWindow ? JoystickTimeEndSeconds : null,
            DeadzoneThreshold, SaturationThreshold, HistogramBins);
        if (result.CoveredSeconds <= 0)
        {
            JoystickUsageStatus = "Geen gezamenlijke meetdekking in dit tijdvenster. Kies een ander venster of controleer de signalen.";
            return;
        }
        JoystickDeadzoneText = UsagePercent(result.DeadzonePercent);
        JoystickSaturationText = UsagePercent(result.SaturationPercent);
        JoystickCoverageText = UsagePercent(100 * result.CoveredSeconds / result.WindowSeconds);
        JoystickUsageStatus = $"{result.CoveredSeconds:N1} s gezamenlijke meetdekking · {Math.Max(0, result.WindowSeconds - result.CoveredSeconds):N1} s zonder dekking overgeslagen. Percentages zijn tijdgewogen.";
        Add(JoystickUsageMetrics, "X-signaal", sx.Label);
        Add(JoystickUsageMetrics, "Y-signaal", sy.Label);
        Add(JoystickUsageMetrics, "Geldige intervallen", result.Points.Count.ToString("N0"));
        Add(JoystickUsageMetrics, "Normalisatie", "Per as: minimum volledige log = −1, maximum = +1; midden van dat bereik = 0.");
        Add(JoystickUsageMetrics, "Interpretatie", "Geschat joystickbereik; geen kalibratie van fysieke nulstand of eindstops. Een constante as wordt op 0 getoond.");
        Add(JoystickUsageMetrics, "Meetgaten", "Intervallen groter dan 5× de mediane sampleafstand per bronsignaal tellen niet mee.");
        Add(JoystickUsageMetrics, "Tijdweging", "Laatst gemeten positie tot de volgende sample, uitsluitend waar beide assen dekking hebben.");
        _currentJoystickFilteredPath = result.Points;
        _currentJoystickDeadzone = DeadzoneThreshold;
        _currentJoystickSaturation = SaturationThreshold;
        _suppressPlaybackAutoRefresh = true;
        try
        {
            JoystickPlaybackStartSeconds = result.Points[0].Time;
            JoystickPlaybackEndSeconds = result.Points[^1].Time + result.Points[^1].Seconds;
            JoystickPlaybackPositionSeconds = JoystickPlaybackEndSeconds;
        }
        finally { _suppressPlaybackAutoRefresh = false; }
        JoystickDensityModel = BuildPointCloud(_currentJoystickFilteredPath, DeadzoneThreshold, SaturationThreshold);
        RadiusHistogramModel = BuildUsageHistogram(result.RadiusDistribution, result.CoveredSeconds, "Joystickuitslag (genormaliseerd)");
        AddRadiusGuides(RadiusHistogramModel, DeadzoneThreshold, SaturationThreshold);
        UpdateJoystickPlaybackPlots();
    }

    private void BuildStrokeUsage()
    {
        StrokeCards.Clear();
        var model = new PlotModel { Title = "Tijd per actuatorpositie", TitleFontSize = 14, IsLegendVisible = true };
        model.Legends.Add(new OxyPlot.Legends.Legend { LegendPosition = OxyPlot.Legends.LegendPosition.TopRight });
        model.Axes.Add(Axis(AxisPosition.Bottom, "Positie binnen logbereik [%]", 0, 100));
        model.Axes.Add(Axis(AxisPosition.Left, "Aandeel gedekte tijd [%]", 0));
        var sides = new[] { ("Links", SelectedActuatorLeftSignal, "#2563EB"), ("Rechts", SelectedActuatorRightSignal, "#D97706"), ("Voor", SelectedActuatorFrontSignal, "#0D9488") };
        foreach (var (side, label, color) in sides)
        {
            StrokeUsageResult? result = null;
            if (TryGetSeries(label, out var series))
                result = UsageAnalytics.AnalyzeStroke(series, UseJoystickTimeWindow ? JoystickTimeStartSeconds : null,
                    UseJoystickTimeWindow ? JoystickTimeEndSeconds : null, StrokeEdgePercent, HistogramBins);
            StrokeCards.Add(new StrokeUsageCard(side, label, color, result));
            if (result is not { CoveredSeconds: > 0, LogMinimum: not null, LogMaximum: not null } || result.LogMaximum <= result.LogMinimum) continue;
            var line = new StairStepSeries { Title = side, Color = OxyColor.Parse(color), StrokeThickness = 2 };
            foreach (var bin in result.Distribution) line.Points.Add(new DataPoint(bin.Start, bin.Seconds * 100 / result.CoveredSeconds));
            line.Points.Add(new DataPoint(100, result.Distribution[^1].Seconds * 100 / result.CoveredSeconds));
            model.Series.Add(line);
        }
        StrokeDistributionModel = model.Series.Count > 0 ? model : EmptyPlot("Geen variërende actuatorposities met meetdekking");
    }

    private static string UsagePercent(double? value) => value.HasValue ? $"{value.Value:N1}%" : "—";

    private static PlotModel BuildUsageHistogram(IReadOnlyList<UsageBin> bins, double covered, string xTitle)
    {
        var model = new PlotModel { Title = "Verdeling joystickuitslag", TitleFontSize = 14 };
        model.Axes.Add(Axis(AxisPosition.Bottom, xTitle, 0, Math.Sqrt(2)));
        model.Axes.Add(Axis(AxisPosition.Left, "Aandeel gedekte tijd [%]", 0));
        var bars = new RectangleBarSeries { FillColor = OxyColor.Parse("#2563EB"), StrokeThickness = 0 };
        foreach (var bin in bins) bars.Items.Add(new RectangleBarItem(bin.Start, 0, bin.End, 100 * bin.Seconds / covered));
        model.Series.Add(bars);
        return model;
    }

    private void BuildActuatorTrackingAnalytics()
    {
        ActuatorMatrix.Clear();
        ActuatorSummary = string.Empty;
        if (!TryGetAnalysisSeries(SelectedJoystickXSignal, out var jx) || !TryGetAnalysisSeries(SelectedJoystickYSignal, out var jy) || !TryGetAnalysisSeries(SelectedJoystickYawSignal, out var jyaw) ||
            !TryGetAnalysisSeries(SelectedActuatorLeftSignal, out var al) || !TryGetAnalysisSeries(SelectedActuatorRightSignal, out var ar) || !TryGetAnalysisSeries(SelectedActuatorFrontSignal, out var af))
        {
            ActuatorLeftOverlayModel = EmptyPlot("Left actuator");
            ActuatorRightOverlayModel = EmptyPlot("Right actuator");
            ActuatorFrontOverlayModel = EmptyPlot("Front actuator");
            ActuatorDelayHistogramModel = EmptyPlot("Delay histogram vlinder");
            return;
        }

        var r = _analyticsService.AnalyzeButterflyKinematics(jx, jy, jyaw, al, ar, af, Math.Max(0, DeadzoneThreshold), Math.Max(0.05, DelaySearchRangeSeconds), Math.Clamp(HistogramBins, 10, 90), 200_000);
        AddActuatorMatrixRow("Correlatie", r.LeftTracking.Correlation, r.RightTracking.Correlation, r.FrontTracking.Correlation);
        AddActuatorMatrixRow("Gain", r.LeftTracking.Gain, r.RightTracking.Gain, r.FrontTracking.Gain);
        ActuatorMatrix.Add(new ActuatorMetricRow("Richting", DirText(r.LeftTracking.Gain), DirText(r.RightTracking.Gain), DirText(r.FrontTracking.Gain)));
        AddActuatorMatrixRow("RMSE (norm)", r.LeftTracking.NormalizedRmse, r.RightTracking.NormalizedRmse, r.FrontTracking.NormalizedRmse);
        ActuatorMatrix.Add(new ActuatorMetricRow("Delay gem [s]", FN(r.LeftDelay.MeanDelaySeconds), FN(r.RightDelay.MeanDelaySeconds), FN(r.FrontDelay.MeanDelaySeconds)));
        ActuatorMatrix.Add(new ActuatorMetricRow("Delay min [s]", FN(r.LeftDelay.MinimumDelaySeconds), FN(r.RightDelay.MinimumDelaySeconds), FN(r.FrontDelay.MinimumDelaySeconds)));
        ActuatorMatrix.Add(new ActuatorMetricRow("Delay max [s]", FN(r.LeftDelay.MaximumDelaySeconds), FN(r.RightDelay.MaximumDelaySeconds), FN(r.FrontDelay.MaximumDelaySeconds)));
        ActuatorMatrix.Add(new ActuatorMetricRow(
            "Events",
            r.LeftDelay.MatchedEventCount.ToString("N0", CultureInfo.CurrentCulture),
            r.RightDelay.MatchedEventCount.ToString("N0", CultureInfo.CurrentCulture),
            r.FrontDelay.MatchedEventCount.ToString("N0", CultureInfo.CurrentCulture)));
        var total = r.LeftDelay.MatchedEventCount + r.RightDelay.MatchedEventCount + r.FrontDelay.MatchedEventCount;
        ActuatorSummary =
            $"Mapping: Left=-Y-Yaw · Right=-Y+Yaw · Front=+X    |    Totaal {total.ToString("N0", CultureInfo.CurrentCulture)} events · " +
            $"delay gem {FN(WeightedMean(r.LeftDelay, r.RightDelay, r.FrontDelay))} s · " +
            $"min {FN(MinDelay(r.LeftDelay, r.RightDelay, r.FrontDelay))} · max {FN(MaxDelay(r.LeftDelay, r.RightDelay, r.FrontDelay))}";
        ActuatorLeftOverlayModel = BuildOverlay(
            "Left actuator: expected vs feedback",
            r.LeftCommandSeries,
            r.LeftResponseSeries,
            OxyColor.Parse("#4E79A7"),
            r.LeftTracking.Gain < 0);
        ActuatorRightOverlayModel = BuildOverlay(
            "Right actuator: expected vs feedback",
            r.RightCommandSeries,
            r.RightResponseSeries,
            OxyColor.Parse("#F28E2B"),
            r.RightTracking.Gain < 0);
        ActuatorFrontOverlayModel = BuildOverlay(
            "Front actuator: expected vs feedback",
            r.FrontCommandSeries,
            r.FrontResponseSeries,
            OxyColor.Parse("#59A14F"),
            r.FrontTracking.Gain < 0);
        ActuatorDelayHistogramModel = BuildButterflyDelayHistogram(r);
    }

    private void AddActuatorMatrixRow(string metric, double left, double right, double front)
        => ActuatorMatrix.Add(new ActuatorMetricRow(metric, F(left), F(right), F(front)));

    private static string DirText(double gain) => gain >= 0 ? "zelfde" : "omgekeerd";

    private void BuildDelayAnalytics()
    {
        DelayMetrics.Clear();
        if (!TryGetAnalysisSeries(SelectedCommandSignal, out var cmd) || !TryGetAnalysisSeries(SelectedResponseSignal, out var rsp))
        {
            _lastDelayResult = null;
            DelayHistogramModel = EmptyPlot("Dode tijd histogram");
            DelayOverlayModel = EmptyPlot("Command/response overlay");
            return;
        }

        var searchRange = Math.Max(0.05, DelaySearchRangeSeconds);
        var r = _analyticsService.AnalyzeDelay(cmd, rsp, searchRange, 0.5, 200_000);
        _lastDelayResult = r;

        var thresholdFraction = Math.Clamp(ResponseThresholdPercent, 0.1, 50.0) / 100.0;
        var fr = _analyticsService.AnalyzeFirstResponseDelay(cmd, rsp, searchRange, thresholdFraction, 30);

        Add(DelayMetrics, "Command", fr.CommandSignalLabel);
        Add(DelayMetrics, "Response", fr.ResponseSignalLabel);
        Add(DelayMetrics, "— Dode tijd: commando → eerste reactie —", $"drempel {F(ResponseThresholdPercent)}% van bereik");
        Add(DelayMetrics, "Dode tijd gem [s]", FN(fr.MeanDeadTimeSeconds));
        Add(DelayMetrics, "Dode tijd min [s]", FN(fr.MinimumDeadTimeSeconds));
        Add(DelayMetrics, "Dode tijd max [s]", FN(fr.MaximumDeadTimeSeconds));
        Add(DelayMetrics, "Dode tijd P95 [s]", FN(fr.Percentile95DeadTimeSeconds));
        Add(DelayMetrics, "Commando-flanken", fr.CommandEdgeCount.ToString("N0", CultureInfo.CurrentCulture));
        Add(DelayMetrics, "Reacties gemeten", fr.MatchedReactionCount.ToString("N0", CultureInfo.CurrentCulture));
        Add(DelayMetrics, "Stijgend dode tijd gem [s]", FN(fr.RisingDeadTime.MeanDelaySeconds));
        Add(DelayMetrics, "Dalend dode tijd gem [s]", FN(fr.FallingDeadTime.MeanDelaySeconds));
        Add(DelayMetrics, "— Kruiscorrelatie (hele golf, ter controle) —", "");
        Add(DelayMetrics, "Lag (correlatie) [s]", F(r.BestLagSeconds));
        Add(DelayMetrics, "Correlatie kwaliteit (-1..1)", F(r.BestCorrelation));
        var deadModel = BuildHistogram("Dode tijd histogram (commando → eerste reactie)", fr.DeadTimeHistogram, "Dode tijd [s]", OxyColor.Parse("#59A14F"));
        AddCumulativeOverlay(deadModel, fr.DeadTimeHistogram);
        DelayHistogramModel = deadModel;
        RefreshDelayOverlayFromCache();
    }

    private void BuildProfessionalCanAnalytics()
    {
        ProfessionalCanMetrics.Clear();
        ProfessionalTopIdRows.Clear();
        ProfessionalCycleRows.Clear();

        if (_dataset is null || _dataset.RawFrames.Count == 0)
        {
            CanFrameRateModel = EmptyPlot("Frames/s over tijd");
            CanBusLoadModel = EmptyPlot("Bus load [%] over tijd");
            CanTopIdPlotModel = EmptyPlot("Top CAN-IDs");
            return;
        }

        var frames = _dataset.RawFrames;
        // Iterate the immutable source order so backwards timestamps remain visible in cycle diagnostics.
        var orderedFrames = frames;
        var start = frames.Min(static frame => frame.TimeSeconds);
        var end = frames.Max(static frame => frame.TimeSeconds);
        var duration = Math.Max(1e-9, end - start);
        var arbitrationBitrate = Math.Max(50, CanBitrateKbps) * 1000.0;
        var dataBitrate = Math.Max(50, CanDataBitrateKbps) * 1000.0;
        var binWidthSeconds = Math.Clamp(CanTimeBinMilliseconds / 1000.0, 0.05, 5.0);
        var requiredBinCount = Math.Max(1L, checked((long)Math.Ceiling(duration / binWidthSeconds) + 1L));
        if (requiredBinCount > 200_000)
        {
            binWidthSeconds = duration / 199_999d;
        }

        var binCount = (int)Math.Min(200_000L, requiredBinCount);
        var frameRateBins = new double[binCount];
        var busTimeBins = new double[binCount];
        var perId = new Dictionary<CanStreamKey, CanIdAccumulator>(256);
        var errorFrames = 0;
        var remoteFrames = 0;
        var totalTransmissionSeconds = 0.0;
        foreach (var frame in orderedFrames)
        {
            var bin = Math.Clamp((int)((frame.TimeSeconds - start) / binWidthSeconds), 0, binCount - 1);
            frameRateBins[bin] += 1;

            var estimatedTransmissionSeconds = EstimateFrameDurationSeconds(frame, arbitrationBitrate, dataBitrate);
            busTimeBins[bin] += estimatedTransmissionSeconds;
            totalTransmissionSeconds += estimatedTransmissionSeconds;

            if (frame.Type.Contains("err", StringComparison.OrdinalIgnoreCase) ||
                frame.Type.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                errorFrames++;
            }

            if (frame.Type.Contains("rtr", StringComparison.OrdinalIgnoreCase) ||
                frame.Type.Contains("remote", StringComparison.OrdinalIgnoreCase))
            {
                remoteFrames++;
            }

            var key = new CanStreamKey(frame.Channel, frame.Direction, frame.FrameFormat, frame.IsExtended, frame.Id);
            if (!perId.TryGetValue(key, out var idStats))
            {
                idStats = new CanIdAccumulator(key);
                perId.Add(key, idStats);
            }

            idStats.Observe(frame);
        }

        var avgFrameRate = orderedFrames.Count / duration;
        var peakFrameRate = frameRateBins.Max() / binWidthSeconds;
        var avgBusLoad = (totalTransmissionSeconds / duration) * 100.0;
        var peakBusLoad = (busTimeBins.Max() / binWidthSeconds) * 100.0;
        var uniqueIds = perId.Keys.Select(static key => key.FrameId).Distinct().Count();
        var extendedCount = orderedFrames.Count(static frame => frame.IsExtended);
        Add(ProfessionalCanMetrics, "Duur log [s]", F(duration));
        Add(ProfessionalCanMetrics, "Totale frames", orderedFrames.Count.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Unieke IDs", uniqueIds.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Unieke frame-reeksen", perId.Count.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Extended frames", extendedCount.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Error frames (indien gelogd)", errorFrames.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Remote/RTR (indien gelogd)", remoteFrames.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Gem frames/s", F(avgFrameRate));
        Add(ProfessionalCanMetrics, "Piek frames/s", F(peakFrameRate));
        Add(ProfessionalCanMetrics, "Gem bus load [%] (geschat)", F(avgBusLoad));
        Add(ProfessionalCanMetrics, "Piek bus load [%] (geschat)", F(peakBusLoad));
        Add(ProfessionalCanMetrics, "Arbitration bitrate [kbps] (aanname)", F(Math.Max(50, CanBitrateKbps)));
        Add(ProfessionalCanMetrics, "FD data bitrate [kbps] (aanname)", F(Math.Max(50, CanDataBitrateKbps)));
        Add(ProfessionalCanMetrics, "Busloadmodel", "CAN/CAN-FD velden + stuffingraming; geschat");
        Add(ProfessionalCanMetrics, "Negatieve cyclustijden", perId.Values.Sum(static item => item.NegativeCycleCount).ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Venster [ms] (instelling)", Math.Clamp(CanTimeBinMilliseconds, 50, 5000).ToString(CultureInfo.CurrentCulture));

        var topByCount = perId.Values
            .OrderByDescending(static stats => stats.Count)
            .ThenBy(static stats => stats.Key.Channel, StringComparer.Ordinal)
            .ThenBy(static stats => stats.Key.FrameId)
            .Take(30)
            .ToArray();
        foreach (var item in topByCount)
        {
            var share = item.Count * 100.0 / Math.Max(1, orderedFrames.Count);
            ProfessionalTopIdRows.Add(new CanTopIdRow(
                item.DisplayKey,
                item.Count.ToString("N0", CultureInfo.CurrentCulture),
                F(share),
                F(item.MeanPayloadLength),
                $"{item.Key.FrameFormat}/{(item.Key.IsExtended ? "Ext" : "Std")}/{item.Key.Direction}"));
        }

        var timingRows = perId.Values
            .Where(static stats => stats.CycleSamples > 0)
            .OrderByDescending(static stats => stats.JitterMs)
            .ThenByDescending(static stats => stats.CycleSamples)
            .Take(40)
            .ToArray();
        foreach (var item in timingRows)
        {
            ProfessionalCycleRows.Add(new CanCycleTimingRow(
                item.DisplayKey,
                item.CycleSamples.ToString("N0", CultureInfo.CurrentCulture),
                F(item.AverageCycleMs),
                F(item.JitterMs),
                F(item.MinCycleMs),
                F(item.MaxCycleMs),
                item.NegativeCycleCount.ToString("N0", CultureInfo.CurrentCulture)));
        }

        CanFrameRateModel = BuildCanTrendPlot(
            "Frames/s over tijd",
            start,
            binWidthSeconds,
            frameRateBins.Select(bin => bin / binWidthSeconds).ToArray(),
            "Frames/s",
            OxyColor.Parse("#4E79A7"));
        CanBusLoadModel = BuildCanTrendPlot(
            "Bus load [%] over tijd (geschat)",
            start,
            binWidthSeconds,
            busTimeBins.Select(seconds => (seconds / binWidthSeconds) * 100.0).ToArray(),
            "Bus load [%]",
            OxyColor.Parse("#E15759"),
            true);
        CanTopIdPlotModel = BuildTopIdBarPlot(topByCount, orderedFrames.Count);
    }

    private void AutoDetectJoystickSignals()
    {
        if (AvailableSignals.Count == 0) return;
        var labels = AvailableSignals.ToList();
        var joy = labels.Where(v => Has(v, ["joy", "joystick"])).ToList();
        SelectedJoystickXSignal = Pick(joy, ["pos_x", "joystick_x", "_x", ".x", " axisx"]) ?? Pick(labels, ["pos_x", "joystick_x", "_x", ".x", " axisx"]);
        SelectedJoystickYSignal = Pick(joy, ["pos_y", "joystick_y", "_y", ".y", " axisy"]) ?? Pick(labels, ["pos_y", "joystick_y", "_y", ".y", " axisy"]);
        SelectedJoystickYawSignal = Pick(joy, ["yaw", "twist", "rz", "rot"]) ?? Pick(labels, ["yaw", "twist", "rz", "rot"]);
    }

    private void AutoDetectButterflySignals()
    {
        AutoDetectJoystickSignals();
        if (AvailableSignals.Count == 0) return;
        var labels = AvailableSignals.ToList();
        SelectedActuatorLeftSignal = BestActuator(labels, ["left", "links"]);
        SelectedActuatorRightSignal = BestActuator(labels, ["right", "rechts"]);
        SelectedActuatorFrontSignal = BestActuator(labels, ["front", "voor"]);
    }

    private void AutoDetectDelaySignals()
    {
        if (AvailableSignals.Count == 0) return;
        var labels = AvailableSignals.ToList();
        var commands = labels.Where(v => Has(v, ["realpos", "cmd", "command", "target", "setpoint", "request", "desired"]) && !Has(v, ["force", "current", "temperature", "status", "error", "heartbeat"])).ToList();
        var responses = labels.Where(v => Has(v, ["actualpos", "actual", "feedback", "measured", "position", "encoder", "state"]) && !Has(v, ["force", "current", "temperature", "status", "error", "heartbeat"])).ToList();
        var prioritized = commands.OrderByDescending(v => Has(v, ["realpos"])).ThenByDescending(v => Has(v, ["cmd", "command", "target", "setpoint"])).ToList();
        foreach (var c in prioritized)
        {
            var match = responses.Where(r => Similar(c, r)).OrderByDescending(r => Has(r, ["actualpos", "actual", "feedback"])).FirstOrDefault();
            if (match is null) continue;
            SelectedCommandSignal = c;
            SelectedResponseSignal = match;
            return;
        }

        SelectedCommandSignal = prioritized.FirstOrDefault() ?? labels.FirstOrDefault();
        SelectedResponseSignal = responses.FirstOrDefault() ?? labels.Skip(1).FirstOrDefault() ?? labels.FirstOrDefault();
    }

    private void ResetJoystickTimeWindow()
    {
        _suppressTimeWindowAutoRecompute = true;
        try
        {
            UseJoystickTimeWindow = false;
            if (JoystickAvailableEndSeconds > JoystickAvailableStartSeconds)
            {
                JoystickTimeStartSeconds = JoystickAvailableStartSeconds;
                JoystickTimeEndSeconds = JoystickAvailableEndSeconds;
            }
        }
        finally
        {
            _suppressTimeWindowAutoRecompute = false;
        }

        _ = RecomputeUsageAsync();
    }

    private bool TryGetAnalysisSeries(string? label, out SignalSeries series)
    {
        series = default!;
        if (_dataset is null || string.IsNullOrWhiteSpace(label) || !_dataset.SignalSeriesByLabel.TryGetValue(label, out var found))
        {
            return false;
        }

        var sourceCount = found.SampleCount ?? 0;
        _largestAnalysisSource = Math.Max(_largestAnalysisSource, sourceCount);
        _analysisWasSampled |= sourceCount > MaximumAnalysisPointsPerSignal;
        if (!_analysisSeriesCache.TryGetValue(label, out series!))
        {
            series = found.ForAnalysis(MaximumAnalysisPointsPerSignal);
            _analysisSeriesCache[label] = series;
        }
        return true;
    }

    private bool TryGetSeries(string? label, out SignalSeries series)
    {
        series = default!;
        if (_dataset is null || string.IsNullOrWhiteSpace(label) || !_dataset.SignalSeriesByLabel.TryGetValue(label, out var found))
        {
            return false;
        }

        series = found;
        return true;
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

    private static PlotModel BuildPointCloud(
        IReadOnlyList<JoystickUsagePoint> points,
        double deadzone,
        double saturation,
        NormalizedPoint? currentPoint = null)
    {
        if (points.Count == 0)
        {
            return EmptyPlot("Joystick puntenwolk (geen data)");
        }

        const double min = -1.2;
        const double max = 1.2;
        const int n = 64;
        var span = max - min;
        var counts = new double[n, n];
        foreach (var p in points)
        {
            var bx = (int)Math.Clamp((p.X - min) / span * n, 0, n - 1);
            var by = (int)Math.Clamp((p.Y - min) / span * n, 0, n - 1);
            counts[bx, by] += p.Seconds;
        }

        var data = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                data[i, j] = counts[i, j] > 0 ? counts[i, j] : double.NaN;
            }
        }

        var model = new PlotModel
        {
            Title = "Waar staat de joystick?",
            TitleFontSize = 14,
            IsLegendVisible = false
        };
        model.Axes.Add(Axis(AxisPosition.Bottom, "Joystick X (genormaliseerd)", min, max));
        model.Axes.Add(Axis(AxisPosition.Left, "Joystick Y (genormaliseerd)", min, max));
        model.Axes.Add(new LinearColorAxis
        {
            Position = AxisPosition.Right,
            Palette = OxyPalette.Interpolate(200, OxyColor.Parse("#93C5FD"), OxyColor.Parse("#2563EB"), OxyColor.Parse("#172554")),
            LowColor = OxyColors.Transparent,
            InvalidNumberColor = OxyColors.White,
            Minimum = 0,
            Title = "Tijd [s]"
        });

        var cellHalf = span / (2.0 * n);
        model.Series.Add(new HeatMapSeries
        {
            X0 = min + cellHalf,
            X1 = max - cellHalf,
            Y0 = min + cellHalf,
            Y1 = max - cellHalf,
            Interpolate = false,
            RenderMethod = HeatMapRenderMethod.Bitmap,
            Data = data
        });

        if (currentPoint is not null)
        {
            var cursor = new ScatterSeries
            {
                Title = "Huidige joystick positie",
                MarkerType = MarkerType.Circle,
                MarkerSize = 4.2,
                MarkerFill = OxyColor.Parse("#2EACF6"),
                MarkerStroke = OxyColors.Black,
                MarkerStrokeThickness = 1.0
            };
            cursor.Points.Add(new ScatterPoint(currentPoint.X, currentPoint.Y));
            model.Series.Add(cursor);
        }

        JoystickGuides(model, deadzone, saturation);
        return model;
    }

    private static PlotModel BuildTrajectory(
        IReadOnlyList<NormalizedPoint> points,
        double deadzone,
        double saturation,
        NormalizedPoint? currentPoint = null)
    {
        if (points.Count == 0) return EmptyPlot("Joystick traject (geen data)");
        var model = new PlotModel { Title = "Traject · laatste 5 seconden", TitleFontSize = 14, IsLegendVisible = false };
        model.Axes.Add(Axis(AxisPosition.Bottom, "Joystick X (genormaliseerd)", -1.2, 1.2));
        model.Axes.Add(Axis(AxisPosition.Left, "Joystick Y (genormaliseerd)", -1.2, 1.2));
        var series = new LineSeries { Title = "Traject", Color = OxyColor.Parse("#4E79A7"), StrokeThickness = 1.2 };
        var step = Math.Max(1, (int)Math.Ceiling(points.Count / 14000.0));
        for (var i = 0; i < points.Count; i += step) series.Points.Add(new DataPoint(points[i].X, points[i].Y));
        var last = points[^1];
        if (series.Points.Count == 0 || series.Points[^1].X != last.X || series.Points[^1].Y != last.Y) series.Points.Add(new DataPoint(last.X, last.Y));
        model.Series.Add(series);
        if (currentPoint is not null)
        {
            var marker = new ScatterSeries
            {
                Title = "Huidige positie",
                MarkerType = MarkerType.Circle,
                MarkerSize = 4.4,
                MarkerFill = OxyColor.Parse("#2EACF6"),
                MarkerStroke = OxyColors.Black,
                MarkerStrokeThickness = 1.0
            };
            marker.Points.Add(new ScatterPoint(currentPoint.X, currentPoint.Y));
            model.Series.Add(marker);
        }

        JoystickGuides(model, deadzone, saturation);
        return model;
    }

    private static PlotModel BuildHistogram(string title, IReadOnlyList<HistogramBin> bins, string xTitle, OxyColor color)
    {
        if (bins.Count == 0) return EmptyPlot($"{title} (geen data)");
        var model = new PlotModel { Title = title };
        model.Axes.Add(Axis(AxisPosition.Bottom, xTitle));
        model.Axes.Add(Axis(AxisPosition.Left, "Aantal events", 0));
        var bars = new RectangleBarSeries { FillColor = color, StrokeThickness = 0 };
        foreach (var b in bins) bars.Items.Add(new RectangleBarItem(b.Start, 0, b.End, b.Count));
        model.Series.Add(bars);
        return model;
    }

    private static void AddRadiusGuides(PlotModel model, double deadzone, double saturation)
    {
        if (deadzone > 0)
        {
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = deadzone,
                Color = OxyColor.Parse("#E15759"),
                LineStyle = LineStyle.Dash,
                StrokeThickness = 1.2,
                Text = "deadzone",
                TextColor = OxyColor.Parse("#E15759")
            });
        }

        if (saturation > deadzone)
        {
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = saturation,
                Color = OxyColor.Parse("#59A14F"),
                LineStyle = LineStyle.Dash,
                StrokeThickness = 1.2,
                Text = "buitenrand",
                TextColor = OxyColor.Parse("#59A14F")
            });
        }
    }

    private static void AddCumulativeOverlay(PlotModel model, IReadOnlyList<HistogramBin> bins)
    {
        if (bins.Count == 0)
        {
            return;
        }

        var total = 0;
        foreach (var b in bins)
        {
            total += b.Count;
        }

        if (total <= 0)
        {
            return;
        }

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Right,
            Key = "cdf",
            Title = "Cumulatief [%]",
            Minimum = 0,
            Maximum = 100,
            MajorGridlineStyle = LineStyle.None,
            MinorGridlineStyle = LineStyle.None
        });

        var line = new LineSeries
        {
            Title = "Cumulatief %",
            Color = OxyColor.Parse("#444444"),
            StrokeThickness = 1.6,
            YAxisKey = "cdf"
        };
        line.Points.Add(new DataPoint(bins[0].Start, 0));
        var cumulative = 0;
        foreach (var b in bins)
        {
            cumulative += b.Count;
            line.Points.Add(new DataPoint(b.End, cumulative * 100.0 / total));
        }

        model.Series.Add(line);
        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            Y = 95,
            YAxisKey = "cdf",
            Color = OxyColor.FromAColor(160, OxyColors.Gray),
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1.0,
            Text = "P95",
            TextColor = OxyColors.Gray
        });
    }

    private static bool IsTimeSorted(IReadOnlyList<RawCanFrame> frames)
    {
        if (frames.Count < 2)
        {
            return true;
        }

        var prev = frames[0].TimeSeconds;
        for (var i = 1; i < frames.Count; i++)
        {
            var current = frames[i].TimeSeconds;
            if (current < prev)
            {
                return false;
            }

            prev = current;
        }

        return true;
    }

    private static double EstimateFrameDurationSeconds(RawCanFrame frame, double arbitrationBitrate, double dataBitrate)
    {
        if (frame.FrameFormat == CanFrameFormat.Classic)
        {
            var bitsWithoutStuffing = (frame.IsExtended ? 67d : 47d) + (frame.PayloadLength * 8d) + 3d;
            return (bitsWithoutStuffing * 1.20d) / arbitrationBitrate;
        }

        var arbitrationBits = (frame.IsExtended ? 41d : 23d) * 1.20d;
        var crcBits = frame.PayloadLength <= 16 ? 17d : 21d;
        var dataPhaseBits = ((frame.PayloadLength * 8d) + crcBits + 13d) * 1.20d;
        var dataPhaseBitrate = frame.BitRateSwitch ? dataBitrate : arbitrationBitrate;
        return (arbitrationBits / arbitrationBitrate) + (dataPhaseBits / dataPhaseBitrate);
    }

    private static PlotModel BuildCanTrendPlot(
        string title,
        double startTime,
        double binWidthSeconds,
        IReadOnlyList<double> values,
        string yTitle,
        OxyColor color,
        bool clampToZero = false)
    {
        if (values.Count == 0)
        {
            return EmptyPlot($"{title} (geen data)");
        }

        var model = new PlotModel { Title = title };
        model.Axes.Add(Axis(AxisPosition.Bottom, "Tijd [s]"));
        model.Axes.Add(clampToZero ? Axis(AxisPosition.Left, yTitle, 0) : Axis(AxisPosition.Left, yTitle));
        var series = new LineSeries { Title = yTitle, Color = color, StrokeThickness = 1.4 };
        var stride = Math.Max(1, values.Count / 5000);
        for (var i = 0; i < values.Count; i += stride)
        {
            var t = startTime + (i * binWidthSeconds);
            series.Points.Add(new DataPoint(t, values[i]));
        }

        var lastIndex = values.Count - 1;
        var lastTime = startTime + (lastIndex * binWidthSeconds);
        if (series.Points.Count == 0 || series.Points[^1].X != lastTime)
        {
            series.Points.Add(new DataPoint(lastTime, values[lastIndex]));
        }

        model.Series.Add(series);
        return model;
    }

    private static PlotModel BuildTopIdBarPlot(IReadOnlyList<CanIdAccumulator> topByCount, int totalFrames)
    {
        if (topByCount.Count == 0)
        {
            return EmptyPlot("Top CAN-IDs (geen data)");
        }

        var shown = topByCount.Take(20).ToArray();
        var model = new PlotModel { Title = "Top CAN-IDs op frame-aandeel" };
        var categoryAxis = new CategoryAxis
        {
            Position = AxisPosition.Left,
            Title = "CAN-ID",
            GapWidth = 0.4
        };

        // Add bottom-to-top so the highest-traffic ID ends up on top.
        for (var i = shown.Length - 1; i >= 0; i--)
        {
            categoryAxis.Labels.Add(shown[i].DisplayKey);
        }

        model.Axes.Add(categoryAxis);
        model.Axes.Add(Axis(AxisPosition.Bottom, "Aandeel [%]", 0));

        var bars = new BarSeries
        {
            FillColor = OxyColor.Parse("#59A14F"),
            StrokeThickness = 0,
            LabelPlacement = LabelPlacement.Outside,
            LabelFormatString = "{0:0.#}%"
        };
        for (var i = shown.Length - 1; i >= 0; i--)
        {
            var share = (shown[i].Count * 100.0) / Math.Max(1, totalFrames);
            bars.Items.Add(new BarItem(share));
        }

        model.Series.Add(bars);
        return model;
    }

    private static PlotModel BuildOverlay(
        string title,
        IReadOnlyList<TimeValuePoint> cmd,
        IReadOnlyList<TimeValuePoint> rsp,
        OxyColor color,
        bool invertResponse)
    {
        if (cmd.Count == 0 || rsp.Count == 0)
        {
            return EmptyPlot($"{title} (geen data)");
        }

        var scaledCommand = ScaleSeriesToReferenceRange(cmd.Select(static p => p.Value).ToArray(), rsp.Select(static p => p.Value).ToArray(), invertResponse);
        var scaledCommandSeries = new List<TimeValuePoint>(Math.Min(cmd.Count, scaledCommand.Length));
        for (var i = 0; i < Math.Min(cmd.Count, scaledCommand.Length); i++)
        {
            scaledCommandSeries.Add(new TimeValuePoint(cmd[i].Time, scaledCommand[i]));
        }

        var model = new PlotModel { Title = title, IsLegendVisible = true };
        model.Axes.Add(Axis(AxisPosition.Bottom, "Tijd [s]"));
        model.Axes.Add(Axis(AxisPosition.Left, "Actuator waarde (verwacht geschaald)"));

        var expectedTitle = invertResponse ? "Expected (geschaald, richting gecorrigeerd)" : "Expected (geschaald)";
        model.Series.Add(BuildStairSeries(expectedTitle, scaledCommandSeries, color, 1.6));
        model.Series.Add(BuildStairSeries("Actual feedback", rsp, OxyColors.Black, 1.2));
        return model;
    }

    private static PlotModel BuildButterflyDelayHistogram(ButterflyKinematicsResult result)
    {
        if (result.DelayHistogramLeft.Count == 0 && result.DelayHistogramRight.Count == 0 && result.DelayHistogramFront.Count == 0) return EmptyPlot("Delay histogram vlinder (geen events)");
        var model = new PlotModel { Title = "Delay histogram per actuator (command -> feedback)", IsLegendVisible = true };
        model.Axes.Add(Axis(AxisPosition.Bottom, "Delta-t [s]", 0));
        model.Axes.Add(Axis(AxisPosition.Left, "Aantal events", 0));
        AddHistLine(model, result.DelayHistogramLeft, "Left", OxyColor.Parse("#4E79A7"));
        AddHistLine(model, result.DelayHistogramRight, "Right", OxyColor.Parse("#F28E2B"));
        AddHistLine(model, result.DelayHistogramFront, "Front", OxyColor.Parse("#59A14F"));
        return model;
    }

    private static void AddHistLine(PlotModel model, IReadOnlyList<HistogramBin> bins, string name, OxyColor color)
    {
        if (bins.Count == 0) return;
        var s = new LineSeries { Title = name, Color = color, StrokeThickness = 2.0 };
        foreach (var b in bins) s.Points.Add(new DataPoint((b.Start + b.End) * 0.5, b.Count));
        model.Series.Add(s);
    }

    private static PlotModel BuildDelayOverlay(
        IReadOnlyList<TimeValuePoint> cmd,
        IReadOnlyList<TimeValuePoint> rsp,
        IReadOnlyList<TimeValuePoint> shifted,
        double maxDelaySeconds,
        bool normalize,
        bool stepPlot,
        bool showSampleMarkers,
        bool showLegend,
        bool showDelayMarkers,
        bool showDelayLabels)
    {
        if (cmd.Count == 0 || rsp.Count == 0)
        {
            return EmptyPlot("Command/response overlay (geen data)");
        }

        var cmdSeries = cmd.ToArray();
        var rspSeries = rsp.ToArray();
        var shiftedSeries = shifted.ToArray();
        if (normalize)
        {
            var cmdValues = cmd.Select(static point => point.Value).ToArray();
            var rspValues = rsp.Select(static point => point.Value).ToArray();
            var shiftedValues = shifted.Select(static point => point.Value).ToArray();
            var cmdNorm = NormalizeSeries(cmdValues);
            var (rspCenter, rspScale) = GetRobustCenterScale(rspValues);
            var rspNorm = NormalizeSeriesWithCenterScale(rspValues, rspCenter, rspScale);
            var shiftedNorm = NormalizeSeriesWithCenterScale(shiftedValues, rspCenter, rspScale);

            cmdSeries = new TimeValuePoint[Math.Min(cmd.Count, cmdNorm.Length)];
            rspSeries = new TimeValuePoint[Math.Min(rsp.Count, rspNorm.Length)];
            shiftedSeries = new TimeValuePoint[Math.Min(shifted.Count, shiftedNorm.Length)];
            for (var i = 0; i < cmdSeries.Length; i++)
            {
                cmdSeries[i] = new TimeValuePoint(cmd[i].Time, cmdNorm[i]);
            }

            for (var i = 0; i < rspSeries.Length; i++)
            {
                rspSeries[i] = new TimeValuePoint(rsp[i].Time, rspNorm[i]);
            }

            for (var i = 0; i < shiftedSeries.Length; i++)
            {
                shiftedSeries[i] = new TimeValuePoint(shifted[i].Time, shiftedNorm[i]);
            }
        }

        var title = normalize
            ? "Command/response overlay (genormaliseerd)"
            : "Command/response overlay (ruwe waarde)";
        var model = new PlotModel { Title = title, IsLegendVisible = showLegend };
        var xAxis = Axis(AxisPosition.Bottom, "Tijd [s]");
        xAxis.IsZoomEnabled = true;
        xAxis.IsPanEnabled = true;
        var yAxis = normalize
            ? Axis(AxisPosition.Left, "Genormaliseerde waarde", -1.2, 1.2)
            : Axis(AxisPosition.Left, "Waarde");
        yAxis.IsZoomEnabled = false;
        yAxis.IsPanEnabled = false;
        model.Axes.Add(xAxis);
        model.Axes.Add(yAxis);
        model.Series.Add(BuildTrendSeries("Command", cmdSeries, OxyColor.Parse("#4E79A7"), 1.5, stepPlot, showSampleMarkers));
        model.Series.Add(BuildTrendSeries("Response", rspSeries, OxyColor.Parse("#E15759"), 1.3, stepPlot, showSampleMarkers));
        model.Series.Add(BuildTrendSeries("Response (shifted)", shiftedSeries, OxyColor.Parse("#59A14F"), 1.2, stepPlot, showSampleMarkers, LineStyle.Dash));
        if (showDelayMarkers)
        {
            AddDelayMarkers(
                model,
                cmdSeries,
                rspSeries,
                Math.Max(0.05, maxDelaySeconds),
                OxyColor.Parse("#4E79A7"),
                OxyColor.Parse("#E15759"),
                showDelayLabels ? 90 : 220,
                thresholdFraction: 0.028,
                minGapMultiplier: 6.0,
                allowOppositeDirectionFallback: false,
                showDelayLabels: showDelayLabels);
        }

        return model;
    }

    private static StairStepSeries BuildStairSeries(
        string title,
        IReadOnlyList<TimeValuePoint> points,
        OxyColor color,
        double thickness,
        LineStyle lineStyle = LineStyle.Solid)
    {
        var stair = new StairStepSeries
        {
            Title = title,
            Color = color,
            StrokeThickness = thickness,
            LineStyle = lineStyle
        };
        foreach (var point in points)
        {
            stair.Points.Add(new DataPoint(point.Time, point.Value));
        }

        return stair;
    }

    private static LineSeries BuildLineSeries(
        string title,
        IReadOnlyList<TimeValuePoint> points,
        OxyColor color,
        double thickness,
        LineStyle lineStyle = LineStyle.Solid)
    {
        var line = new LineSeries
        {
            Title = title,
            Color = color,
            StrokeThickness = thickness,
            LineStyle = lineStyle
        };
        foreach (var point in points)
        {
            line.Points.Add(new DataPoint(point.Time, point.Value));
        }

        return line;
    }

    private static void ApplySampleMarkerStyle(LineSeries series, bool enabled)
    {
        if (!enabled)
        {
            series.MarkerType = MarkerType.None;
            return;
        }

        series.MarkerType = MarkerType.Circle;
        series.MarkerSize = 2.0;
        series.MarkerStroke = OxyColors.Black;
        series.MarkerStrokeThickness = 0.5;
        series.MarkerFill = series.Color;
    }

    private static Series BuildTrendSeries(
        string title,
        IReadOnlyList<TimeValuePoint> points,
        OxyColor color,
        double thickness,
        bool stepPlot,
        bool showSampleMarkers,
        LineStyle lineStyle = LineStyle.Solid)
    {
        if (stepPlot)
        {
            var stair = BuildStairSeries(title, points, color, thickness, lineStyle);
            ApplySampleMarkerStyle(stair, showSampleMarkers);
            return stair;
        }

        var line = BuildLineSeries(title, points, color, thickness, lineStyle);
        ApplySampleMarkerStyle(line, showSampleMarkers);
        return line;
    }

    private static void AddDelayMarkers(
        PlotModel model,
        IReadOnlyList<TimeValuePoint> commandSeries,
        IReadOnlyList<TimeValuePoint> responseSeries,
        double maxDelaySeconds,
        OxyColor commandColor,
        OxyColor responseColor,
        int maxVisualMatches,
        double thresholdFraction,
        double minGapMultiplier,
        bool allowOppositeDirectionFallback,
        bool showDelayLabels)
    {
        var matches = BuildDelayVisualMatches(
            commandSeries,
            responseSeries,
            maxDelaySeconds,
            maxVisualMatches,
            thresholdFraction,
            minGapMultiplier,
            allowOppositeDirectionFallback);
        if (matches.Count == 0)
        {
            return;
        }

        var connections = new LineSeries
        {
            Title = "Delay koppelingen",
            Color = OxyColor.FromAColor(140, OxyColors.Gray),
            StrokeThickness = 1.0,
            LineStyle = LineStyle.Dash
        };
        var commandMarkers = new ScatterSeries
        {
            Title = "Command events",
            MarkerType = MarkerType.Triangle,
            MarkerFill = commandColor,
            MarkerStroke = commandColor,
            MarkerSize = 2.7
        };
        var responseMarkers = new ScatterSeries
        {
            Title = "Response events",
            MarkerType = MarkerType.Diamond,
            MarkerFill = responseColor,
            MarkerStroke = responseColor,
            MarkerSize = 2.5
        };

        foreach (var match in matches)
        {
            commandMarkers.Points.Add(new ScatterPoint(match.Command.Time, match.Command.Value));
            responseMarkers.Points.Add(new ScatterPoint(match.Response.Time, match.Response.Value));

            connections.Points.Add(new DataPoint(match.Command.Time, match.Command.Value));
            connections.Points.Add(new DataPoint(match.Response.Time, match.Response.Value));
            connections.Points.Add(DataPoint.Undefined);

            if (showDelayLabels)
            {
                var midX = (match.Command.Time + match.Response.Time) * 0.5;
                var midY = (match.Command.Value + match.Response.Value) * 0.5;
                model.Annotations.Add(new TextAnnotation
                {
                    Text = $"dt={match.DelaySeconds.ToString("0.###", CultureInfo.CurrentCulture)} s",
                    TextPosition = new DataPoint(midX, midY),
                    TextHorizontalAlignment = HorizontalAlignment.Center,
                    TextVerticalAlignment = VerticalAlignment.Bottom,
                    Stroke = OxyColors.Undefined,
                    Background = OxyColor.FromAColor(145, OxyColors.White),
                    TextColor = OxyColor.Parse("#4A4A4A"),
                    FontSize = 10
                });
            }
        }

        model.Series.Add(connections);
        model.Series.Add(commandMarkers);
        model.Series.Add(responseMarkers);
    }

    private static IReadOnlyList<DelayVisualMatch> BuildDelayVisualMatches(
        IReadOnlyList<TimeValuePoint> commandSeries,
        IReadOnlyList<TimeValuePoint> responseSeries,
        double maxDelaySeconds,
        int maxVisualMatches,
        double thresholdFraction,
        double minGapMultiplier,
        bool allowOppositeDirectionFallback)
    {
        var commandEvents = DetectChangeEvents(commandSeries, maxVisualMatches * 6, thresholdFraction, minGapMultiplier);
        var responseEvents = DetectChangeEvents(responseSeries, maxVisualMatches * 6, thresholdFraction, minGapMultiplier);
        if (commandEvents.Count == 0 || responseEvents.Count == 0)
        {
            return [];
        }

        var matches = new List<DelayVisualMatch>(Math.Min(commandEvents.Count, maxVisualMatches));
        var responseStart = 0;
        foreach (var commandEvent in commandEvents)
        {
            while (responseStart < responseEvents.Count && responseEvents[responseStart].Time < commandEvent.Time)
            {
                responseStart++;
            }

            for (var j = responseStart; j < responseEvents.Count; j++)
            {
                var candidate = responseEvents[j];
                var dt = candidate.Time - commandEvent.Time;
                if (dt < 0)
                {
                    continue;
                }

                if (dt > maxDelaySeconds)
                {
                    break;
                }

                if (candidate.Direction != commandEvent.Direction)
                {
                    if (allowOppositeDirectionFallback)
                    {
                        continue;
                    }

                    continue;
                }

                matches.Add(new DelayVisualMatch(commandEvent, candidate, dt));
                responseStart = j + 1;
                break;
            }

            if (matches.Count >= maxVisualMatches)
            {
                break;
            }            
        }

        return matches;
    }

    private static IReadOnlyList<ChangeEvent> DetectChangeEvents(
        IReadOnlyList<TimeValuePoint> series,
        int maxEvents,
        double thresholdFraction,
        double minGapMultiplier)
    {
        if (series.Count < 2)
        {
            return [];
        }

        var values = series.Select(static p => p.Value).ToArray();
        var sortedValues = values.OrderBy(static v => v).ToArray();
        var range = Math.Max(1e-9, Percentile(sortedValues, 0.99) - Percentile(sortedValues, 0.01));
        var absDeltas = new double[series.Count - 1];
        for (var i = 1; i < series.Count; i++)
        {
            absDeltas[i - 1] = Math.Abs(series[i].Value - series[i - 1].Value);
        }

        Array.Sort(absDeltas);
        var noiseFloor = Percentile(absDeltas, 0.80);
        var highThreshold = Math.Max(1e-5, Math.Max(range * Math.Clamp(thresholdFraction, 0.01, 0.5), noiseFloor * 2.5));
        var lowThreshold = highThreshold * 0.45;
        var medianDt = EstimateMedianDt(series);
        var minGapSeconds = Math.Max(0.03, medianDt * Math.Max(3.0, minGapMultiplier));

        var events = new List<ChangeEvent>();
        var lastEventTime = double.NegativeInfinity;
        for (var i = 1; i < series.Count; i++)
        {
            var delta = series[i].Value - series[i - 1].Value;
            if (Math.Abs(delta) < highThreshold)
            {
                continue;
            }

            if (Math.Abs(series[i - 1].Value - series[Math.Max(0, i - 2)].Value) >= lowThreshold)
            {
                continue;
            }

            var time = series[i].Time;
            if ((time - lastEventTime) < minGapSeconds)
            {
                continue;
            }

            var direction = Math.Sign(delta);
            if (direction == 0)
            {
                continue;
            }

            var persistent = false;
            var iEnd = Math.Min(series.Count - 1, i + 2);
            for (var k = i; k <= iEnd; k++)
            {
                var d = series[k].Value - series[k - 1].Value;
                if (Math.Sign(d) == direction && Math.Abs(d) >= lowThreshold)
                {
                    persistent = true;
                    break;
                }
            }

            if (!persistent)
            {
                continue;
            }

            events.Add(new ChangeEvent(time, series[i].Value, direction >= 0 ? 1 : -1, Math.Abs(delta)));
            lastEventTime = time;
        }

        if (events.Count <= maxEvents)
        {
            return events;
        }

        var stride = Math.Max(1, events.Count / maxEvents);
        var reduced = new List<ChangeEvent>(maxEvents + 1);
        for (var i = 0; i < events.Count; i += stride)
        {
            reduced.Add(events[i]);
        }

        if (reduced[^1].Time != events[^1].Time)
        {
            reduced.Add(events[^1]);
        }

        return reduced;
    }

    private static double EstimateMedianDt(IReadOnlyList<TimeValuePoint> series)
    {
        if (series.Count < 2)
        {
            return 0.01;
        }

        var deltas = new List<double>(series.Count - 1);
        for (var i = 1; i < series.Count; i++)
        {
            var dt = series[i].Time - series[i - 1].Time;
            if (dt > 0 && !double.IsNaN(dt) && !double.IsInfinity(dt))
            {
                deltas.Add(dt);
            }
        }

        if (deltas.Count == 0)
        {
            return 0.01;
        }

        deltas.Sort();
        var mid = deltas.Count / 2;
        return deltas.Count % 2 == 0 ? (deltas[mid - 1] + deltas[mid]) * 0.5 : deltas[mid];
    }

    private static double[] ScaleSeriesToReferenceRange(
        IReadOnlyList<double> source,
        IReadOnlyList<double> reference,
        bool invertDirection)
    {
        if (source.Count == 0 || reference.Count == 0)
        {
            return [];
        }

        var sourceSorted = source.OrderBy(static v => v).ToArray();
        var referenceSorted = reference.OrderBy(static v => v).ToArray();
        var sourceCenter = (Percentile(sourceSorted, 0.01) + Percentile(sourceSorted, 0.99)) * 0.5;
        var sourceScale = Math.Max(1e-9, (Percentile(sourceSorted, 0.99) - Percentile(sourceSorted, 0.01)) * 0.5);
        var referenceCenter = (Percentile(referenceSorted, 0.01) + Percentile(referenceSorted, 0.99)) * 0.5;
        var referenceScale = Math.Max(1e-9, (Percentile(referenceSorted, 0.99) - Percentile(referenceSorted, 0.01)) * 0.5);

        var scaled = new double[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var normalized = (source[i] - sourceCenter) / sourceScale;
            if (invertDirection)
            {
                normalized = -normalized;
            }

            scaled[i] = referenceCenter + (normalized * referenceScale);
        }

        return scaled;
    }

    private static void JoystickGuides(PlotModel model, double deadzoneThreshold, double saturationThreshold)
    {
        var dz = Math.Clamp(deadzoneThreshold, 0, 2); var sat = Math.Clamp(saturationThreshold, 0, 2);
        model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = 0, Color = OxyColor.FromAColor(140, OxyColors.DimGray), LineStyle = LineStyle.Dot });
        model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, Y = 0, Color = OxyColor.FromAColor(140, OxyColors.DimGray), LineStyle = LineStyle.Dot });
        model.Annotations.Add(new EllipseAnnotation { X = 0, Y = 0, Width = 2.0, Height = 2.0, Stroke = OxyColors.Black, StrokeThickness = 1.0, Fill = OxyColors.Transparent });
        if (dz > 0) model.Annotations.Add(new EllipseAnnotation { X = 0, Y = 0, Width = dz * 2, Height = dz * 2, Stroke = OxyColor.Parse("#E15759"), StrokeThickness = 1.2, Fill = OxyColors.Transparent });
        if (sat > dz) model.Annotations.Add(new EllipseAnnotation { X = 0, Y = 0, Width = sat * 2, Height = sat * 2, Stroke = OxyColor.Parse("#59A14F"), StrokeThickness = 1.2, Fill = OxyColors.Transparent });
    }

    private static Axis Axis(AxisPosition pos, string title, double min = double.NaN, double max = double.NaN) => new LinearAxis
    {
        Position = pos,
        Title = title,
        Minimum = min,
        Maximum = max,
        MajorGridlineStyle = LineStyle.Solid,
        MinorGridlineStyle = LineStyle.Dot,
        MajorGridlineColor = OxyColor.FromAColor(80, OxyColors.Gray),
        MinorGridlineColor = OxyColor.FromAColor(40, OxyColors.Gray)
    };

    private static PlotModel EmptyPlot(string title)
    {
        var m = new PlotModel { Title = title };
        m.Axes.Add(Axis(AxisPosition.Bottom, "X"));
        m.Axes.Add(Axis(AxisPosition.Left, "Y"));
        return m;
    }

    private static void Add(ICollection<MetricRow> list, string n, string v) => list.Add(new MetricRow(n, v));
    private static string F(double value) => value.ToString("G15", CultureInfo.CurrentCulture);
    private static string FN(double? value) => value.HasValue ? F(value.Value) : "-";

    private static double? WeightedMean(params DelayEventStatistics[] stats)
    {
        double weighted = 0; var count = 0;
        foreach (var s in stats) { if (!s.MeanDelaySeconds.HasValue || s.MatchedEventCount <= 0) continue; weighted += s.MeanDelaySeconds.Value * s.MatchedEventCount; count += s.MatchedEventCount; }
        return count == 0 ? null : weighted / count;
    }

    private static double? MinDelay(params DelayEventStatistics[] stats)
    {
        var values = stats.Select(v => v.MinimumDelaySeconds).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return values.Length == 0 ? null : values.Min();
    }

    private static double? MaxDelay(params DelayEventStatistics[] stats)
    {
        var values = stats.Select(v => v.MaximumDelaySeconds).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    private static string SensitivityAdvice(JoystickPairStatistics stats)
    {
        var reasons = new List<string>();
        if (stats.UsedXRangePercent < 45 || stats.UsedYRangePercent < 45) reasons.Add("slag wordt beperkt gebruikt: gevoeligheid iets verhogen of expo aanpassen");
        if (stats.DeadzonePercent > 40) reasons.Add("veel tijd in deadzone: deadzone verkleinen of gain verhogen");
        if (stats.SaturationPercent > 15) reasons.Add("vaak in eindstop: gevoeligheid verlagen of output limiter toepassen");
        if (Math.Abs(stats.BiasX) > 0.15 || Math.Abs(stats.BiasY) > 0.15) reasons.Add("center-offset zichtbaar: joystick kalibreren");
        if (reasons.Count == 0) return "Instelling lijkt gezond; geen directe tuning nodig.";
        var sb = new StringBuilder();
        for (var i = 0; i < reasons.Count; i++) { if (i > 0) sb.Append("; "); sb.Append(reasons[i]); }
        return sb.ToString();
    }

    private static bool Has(string text, IReadOnlyList<string> tokens)
    {
        var lower = text.ToLowerInvariant();
        return tokens.Any(t => lower.Contains(t.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private static bool Similar(string left, string right)
    {
        var l = left.ToLowerInvariant(); var r = right.ToLowerInvariant();
        var replace = new[] { ".x", ".y", ".z", "_x", "_y", "_z", "axisx", "axisy", "axisz", "cmd", "command", "target", "setpoint", "request", "desired", "realpos", "actual", "actualpos", "feedback", "measured", "position", "encoder", "joy", "joystick", "actuator" };
        foreach (var token in replace) { l = l.Replace(token, string.Empty, StringComparison.Ordinal); r = r.Replace(token, string.Empty, StringComparison.Ordinal); }
        return string.Equals(l.Trim(), r.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Pick(IReadOnlyList<string> labels, IReadOnlyList<string> tokens) => labels.FirstOrDefault(l => Has(l, tokens));

    private static string? BestActuator(IReadOnlyList<string> labels, IReadOnlyList<string> sideTokens)
    {
        var ranked = labels.Where(l => sideTokens.Any(t => l.Contains(t, StringComparison.OrdinalIgnoreCase)) && (l.Contains("pos", StringComparison.OrdinalIgnoreCase) || l.Contains("position", StringComparison.OrdinalIgnoreCase)) && !new[] { "target", "setpoint", "command", "desired" }.Any(t => l.Contains(t, StringComparison.OrdinalIgnoreCase))).Select(l => new { Label = l, Score = ActuatorScore(l, sideTokens) }).OrderByDescending(v => v.Score).ThenBy(v => v.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        return ranked.Count == 0 || ranked[0].Score <= 0 ? null : ranked[0].Label;
    }

    private static int ActuatorScore(string label, IReadOnlyList<string> sideTokens)
    {
        var score = 0;
        if (Has(label, sideTokens)) score += 6;
        if (Has(label, ["actualpos", "realpos", "position", "feedback", "actuator", "act"])) score += 5;
        if (Has(label, ["force", "current", "temperature", "voltage", "status", "state", "error", "heartbeat", "text", "char"])) score -= 8;
        return score;
    }

    private static double[] NormalizeSeries(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var (center, scale) = GetRobustCenterScale(values);
        return NormalizeSeriesWithCenterScale(values, center, scale);
    }

    private static (double Center, double Scale) GetRobustCenterScale(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return (0, 1);
        }

        var sorted = values.OrderBy(static v => v).ToArray();
        var p01 = Percentile(sorted, 0.01);
        var p99 = Percentile(sorted, 0.99);
        return ((p01 + p99) * 0.5, Math.Max(1e-9, (p99 - p01) * 0.5));
    }

    private static double[] NormalizeSeriesWithCenterScale(IReadOnlyList<double> values, double center, double scale)
    {
        var normalized = new double[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            normalized[i] = Math.Clamp((values[i] - center) / scale, -1.2, 1.2);
        }

        return normalized;
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double q)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        var fraction = Math.Clamp(q, 0, 1);
        var pos = fraction * (sortedValues.Count - 1);
        var i0 = (int)Math.Floor(pos);
        var i1 = (int)Math.Ceiling(pos);
        if (i0 == i1)
        {
            return sortedValues[i0];
        }

        var t = pos - i0;
        return sortedValues[i0] + ((sortedValues[i1] - sortedValues[i0]) * t);
    }

    private static IPlotController CreateInteractiveController()
    {
        var controller = new PlotController();
        controller.UnbindAll();
        var reverseAwareZoomCommand = new DelegatePlotCommand<OxyMouseDownEventArgs>((view, ctrl, args) =>
        {
            ctrl.AddMouseManipulator(view, new ReverseZoomRectangleManipulator(view), args);
        });
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Left, OxyModifierKeys.None, 1), reverseAwareZoomCommand);
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Right, OxyModifierKeys.None, 1), PlotCommands.PanAt);
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Left, OxyModifierKeys.None, 2), PlotCommands.ResetAt);
        controller.Bind(new OxyMouseWheelGesture(OxyModifierKeys.None), PlotCommands.ZoomWheel);
        return controller;
    }

    private sealed record CanStreamKey(
        string Channel,
        CanFrameDirection Direction,
        CanFrameFormat FrameFormat,
        bool IsExtended,
        uint FrameId);

    private sealed class CanIdAccumulator(CanStreamKey key)
    {
        private double _lastTimeSeconds = double.NaN;
        private double _cycleM2Ms;
        private double _cycleMeanMs;

        public CanStreamKey Key { get; } = key;
        public string DisplayKey => $"{(string.IsNullOrWhiteSpace(Key.Channel) ? "?" : Key.Channel)} / 0x{Key.FrameId:X}";
        public int Count { get; private set; }
        public int PayloadLengthSum { get; private set; }
        public int CycleSamples { get; private set; }
        public int NegativeCycleCount { get; private set; }
        public double MinCycleMs { get; private set; } = double.PositiveInfinity;
        public double MaxCycleMs { get; private set; } = double.NegativeInfinity;

        public double MeanPayloadLength => Count <= 0 ? 0 : PayloadLengthSum / (double)Count;
        public double AverageCycleMs => CycleSamples <= 0 ? 0 : _cycleMeanMs;
        public double JitterMs => CycleSamples <= 1 ? 0 : Math.Sqrt(_cycleM2Ms / (CycleSamples - 1));

        public void Observe(RawCanFrame frame)
        {
            Count++;
            PayloadLengthSum += frame.PayloadLength;

            if (!double.IsNaN(_lastTimeSeconds))
            {
                var cycleMs = (frame.TimeSeconds - _lastTimeSeconds) * 1000.0;
                if (cycleMs < 0)
                {
                    NegativeCycleCount++;
                }
                ObserveCycle(cycleMs);
            }

            _lastTimeSeconds = frame.TimeSeconds;
        }

        private void ObserveCycle(double cycleMs)
        {
            CycleSamples++;
            MinCycleMs = Math.Min(MinCycleMs, cycleMs);
            MaxCycleMs = Math.Max(MaxCycleMs, cycleMs);
            var delta = cycleMs - _cycleMeanMs;
            _cycleMeanMs += delta / CycleSamples;
            var delta2 = cycleMs - _cycleMeanMs;
            _cycleM2Ms += delta * delta2;
        }
    }

    private sealed record ChangeEvent(double Time, double Value, int Direction, double Magnitude);
    private sealed record DelayVisualMatch(ChangeEvent Command, ChangeEvent Response, double DelaySeconds);

}
