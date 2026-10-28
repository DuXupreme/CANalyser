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
    private readonly ITelemetryService _telemetryService;
    private readonly IJoystickAnalyticsService _analyticsService;
    private readonly DispatcherTimer _joystickPlaybackTimer;
    private CanDataset? _dataset;
    private DelayAnalysisResult? _lastDelayResult;
    private FirstResponseDelayResult? _exportFirstResponse;
    private JoystickUsageResult? _exportJoystick;
    private readonly List<object> _exportTracking = [];
    private object? _exportCan;
    private bool _suppressTimeWindowAutoRecompute;
    private bool _suppressPlaybackAutoRefresh;
    private bool _loadingDataset;
    private int _analysisRevision;
    private bool _recomputeRequested;
    private bool _usageDirty = true, _trackingDirty = true, _reactionDirty = true, _canDirty = true;
    private readonly List<string> _failedAnalyses = [];
    private DateTime _lastPlaybackTickUtc;
    private IReadOnlyList<JoystickUsagePoint> _currentJoystickFilteredPath = [];
    private double _currentJoystickDeadzone;
    private double _currentJoystickSaturation;


    [ObservableProperty] private string _joystickUsageStatus = "Laad een log om joystickgebruik en actuatorbereik te bekijken.";
    [ObservableProperty] private string _joystickDeadzoneText = "—";
    [ObservableProperty] private string _joystickSaturationText = "—";
    [ObservableProperty] private string _joystickCoverageText = "—";
    [ObservableProperty] private double _strokeEdgePercent = 5;
    public IReadOnlyList<ActuatorRangeSettings> ActuatorRanges { get; } = [new("Links"), new("Rechts"), new("Voor")];
    public ObservableCollection<string> AvailableCanChannels { get; } = [];
    [ObservableProperty] private string? _selectedCanChannel;
    public ObservableCollection<string> OptionalSignals { get; } = [string.Empty];
    [ObservableProperty] private double _usageMaximumGapSeconds = 0.5;
    [ObservableProperty] private bool _useJoystickCalibration;
    [ObservableProperty] private double _joystickMinimum = -1;
    [ObservableProperty] private double _joystickMaximum = 1;
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
    [ObservableProperty] private bool _delayOverlayShowDelayMarkers;
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
        _telemetryService = telemetryService;
        ActiveUsage = activeUsage;
        _analyticsService = analyticsService;
        foreach (var range in ActuatorRanges) range.PropertyChanged += (_, args) =>
        {
            InvalidateUsage(); InvalidateTracking(); InvalidateDelay();
            if (args.PropertyName == nameof(ActuatorRangeSettings.SetpointSignal) && !_loadingDataset && !_syncingReactionPair)
                SelectReactionPairFromActuator();
        };
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
        RefreshReferenceSummaries();
        ReactionReferenceExplanation = ReferenceExplanation(true);
    }

    public ActiveUsageViewModel ActiveUsage { get; }

    public object CaptureExport() => new
    {
        Scope = new { UseJoystickTimeWindow, JoystickTimeStartSeconds, JoystickTimeEndSeconds },
        Settings = new { SelectedJoystickXSignal, SelectedJoystickYSignal, SelectedActuatorLeftSignal,
            SelectedActuatorRightSignal, SelectedActuatorFrontSignal, SelectedCommandSignal, SelectedResponseSignal,
            UseJoystickCalibration, JoystickMinimum, JoystickMaximum, UsageMaximumGapSeconds, StrokeEdgePercent,
            DeadzoneThreshold, SaturationThreshold, HistogramBins, DelaySearchRangeSeconds, ResponseThresholdPercent,
            UseAutomaticResponseThreshold, ResponseThresholdAbsolute, SelectedReactionActuator, SelectedReactionKind,
            SelectedReactionReference, UseAutomaticPositionTolerance, PositionToleranceAbsolute, CalculateCorrelation,
            SelectedCanChannel, CanBitrateKbps, CanDataBitrateKbps, CanTimeBinMilliseconds, ActuatorRanges },
        Joystick = new { Status = JoystickUsageStatus, Calibrated = UseJoystickCalibration,
            Result = _exportJoystick is null ? null : new { _exportJoystick.CoveredSeconds, _exportJoystick.WindowSeconds,
                DeadzonePercent = UseJoystickCalibration ? _exportJoystick.DeadzonePercent : null,
                SaturationPercent = UseJoystickCalibration ? _exportJoystick.SaturationPercent : null,
                _exportJoystick.RadiusDistribution }, UsageDetails = CaptureJoystickUsageSummary() },
        Stroke = StrokeCards.Select(x => new { x.Name, x.Signal, x.ReferenceNote, x.Result }).ToArray(),
        Tracking = new { Status = ActuatorSummary, Results = _exportTracking.ToArray(), DisplayMetrics = ActuatorMatrix.ToArray() },
        Delay = new { Method = "source-sample-dual-reference-v2", Correlation = _lastDelayResult,
            FirstResponse = _exportFirstResponse, MotionResponse = _exportMotionResponse,
            SetpointChange = new { Reference = "SetpointChange", Result = _exportMotionResponse },
            PositionRequest = new { Reference = "PositionRequest", Result = _exportPositionRequest },
            MotionTracking = _exportMotionTracking, DisplayMetrics = DelayMetrics.ToArray() },
        Can = new { Scope = "Complete dataset, selected CAN channel", Result = _exportCan, DisplayMetrics = ProfessionalCanMetrics.ToArray(),
            TopIds = ProfessionalTopIdRows.ToArray(), CycleTiming = ProfessionalCycleRows.ToArray(),
            FrameRate = ExportLines(CanFrameRateModel), BusLoad = ExportLines(CanBusLoadModel) }
    };

    private static object[] ExportLines(PlotModel model) => model.Series.OfType<LineSeries>()
        .Select(s => (object)new { s.Title, Points = s.Points.Select(p => new { X = double.IsFinite(p.X) ? (double?)p.X : null, Y = double.IsFinite(p.Y) ? (double?)p.Y : null }).ToArray() }).ToArray();

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
        JoystickModuleResult = null;
        _usageDirty = true;
        _analysisRevision++;
        _exportJoystick = null;
        ClearJoystickUsageSummary();
        StopJoystickPlayback();
        _currentJoystickFilteredPath = [];
        StrokeCards.Clear();
        JoystickUsageMetrics.Clear();
        JoystickDeadzoneText = JoystickSaturationText = JoystickCoverageText = "—";
        JoystickUsageStatus = "Instellingen gewijzigd; analyse wordt automatisch bijgewerkt.";
        JoystickDensityModel = EmptyPlot("Bereken analyse");
        RadiusHistogramModel = EmptyPlot("Bereken analyse");
        TrajectoryModel = EmptyPlot("Bereken analyse");
        StrokeDistributionModel = EmptyPlot("Bereken analyse");
        ScheduleAutomaticAnalysis();
    }
    private void InvalidateDelay()
    {
        ReactionModuleResult = null;
        InvalidateTracking();
        _reactionDirty = true;
        _analysisRevision++;
        ClearReactionResults();
        _lastDelayResult = null; _exportFirstResponse = null; DelayMetrics.Clear();
        DelayHistogramModel = EmptyPlot("Herbereken latency"); DelayOverlayModel = EmptyPlot("Herbereken latency");
        ScheduleAutomaticAnalysis();
    }
    partial void OnSelectedCommandSignalChanged(string? value) { InvalidateDelay(); SyncReactionPairToActuator(); RefreshPositionReferenceConfirmation(); }
    partial void OnSelectedResponseSignalChanged(string? value) { InvalidateDelay(); SyncReactionPairToActuator(); RefreshPositionReferenceConfirmation(); }
    partial void OnDelaySearchRangeSecondsChanged(double value) => InvalidateDelay();
    partial void OnSelectedJoystickXSignalChanged(string? value) { UseJoystickCalibration = false; InvalidateUsage(); }
    partial void OnSelectedJoystickYSignalChanged(string? value) { UseJoystickCalibration = false; InvalidateUsage(); }
    partial void OnSelectedActuatorLeftSignalChanged(string? value) { InvalidateUsage(); InvalidateTracking(); RefreshSelectedActuatorFeedback(); }
    partial void OnSelectedActuatorRightSignalChanged(string? value) { InvalidateUsage(); InvalidateTracking(); RefreshSelectedActuatorFeedback(); }
    partial void OnSelectedActuatorFrontSignalChanged(string? value) { InvalidateUsage(); InvalidateTracking(); RefreshSelectedActuatorFeedback(); }
    partial void OnHistogramBinsChanged(int value) => InvalidateUsage();
    partial void OnDeadzoneThresholdChanged(double value) => InvalidateUsage();
    partial void OnSaturationThresholdChanged(double value) => InvalidateUsage();
    partial void OnUsageMaximumGapSecondsChanged(double value) { InvalidateUsage(); InvalidateTracking(); InvalidateDelay(); }
    partial void OnUseJoystickCalibrationChanged(bool value) => InvalidateUsage();
    partial void OnJoystickMinimumChanged(double value) => InvalidateUsage();
    partial void OnJoystickMaximumChanged(double value) => InvalidateUsage();
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
            InvalidateDelay();
            _ = RecomputeAsync();
        }
    }

    partial void OnDelayOverlayNormalizeChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnDelayOverlayStepPlotChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnDelayOverlaySampleMarkersChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnDelayOverlayShowLegendChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnDelayOverlayShowDelayMarkersChanged(bool value) => RefreshDelayOverlayFromCache();
    partial void OnSelectedCanChannelChanged(string? value) => RefreshProfessionalCanAnalyticsFromSettings();
    partial void OnCanBitrateKbpsChanged(double value)
    {
        if (!_loadingDataset) CanBitratesConfirmed = false;
        if (!double.IsFinite(value) || value < 50)
        {
            CanBitrateKbps = 50;
            return;
        }

        RefreshProfessionalCanAnalyticsFromSettings();
    }

    partial void OnCanDataBitrateKbpsChanged(double value)
    {
        if (!_loadingDataset) CanBitratesConfirmed = false;
        if (!double.IsFinite(value) || value < 50)
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
        _loadingDataset = true;
        _usageDirty = _trackingDirty = _reactionDirty = _canDirty = true;
        _analysisRevision++;
        try
        {
            StopJoystickPlayback();
            ClearOutputs();
            _suppressTimeWindowAutoRecompute = true;
            try
            {
                UseJoystickTimeWindow = false;
                JoystickTimeStartSeconds = JoystickTimeEndSeconds = 0;
            }
            finally { _suppressTimeWindowAutoRecompute = false; }
            _dataset = dataset;
            StatusText = "Dataset geladen; analyses worden automatisch berekend…";
            UseJoystickCalibration = false;
            ActiveUsage.LoadDataset(dataset);
            AvailableSignals.Clear();
            OptionalSignals.Clear(); OptionalSignals.Add(string.Empty);
            foreach (var range in ActuatorRanges)
            {
                range.CenterSignal = range.SetpointSignal = range.HalfRangeSignal = range.RunSignal = null;
                range.ManualCenter = null; range.HalfRange = null;
            }
            foreach (var label in dataset.SignalLabels) { AvailableSignals.Add(label); OptionalSignals.Add(label); }
            AvailableCanChannels.Clear();
            foreach (var channel in dataset.Channels.Where(c => dataset.ImportReport?.InternalDiagnosticChannels.Contains(c, StringComparer.OrdinalIgnoreCase) != true))
                AvailableCanChannels.Add(channel);
            SelectedCanChannel = AvailableCanChannels.FirstOrDefault();
            AutoDetectButterflySignals();
            ApplyDatasetProfile(dataset);
        }
        finally { _loadingDataset = false; }
        _ = RecomputeAsync();
    }

    private async Task RecomputeAsync()
    {
        if (IsBusy)
        {
            _recomputeRequested = true;
            return;
        }

        if (_dataset is null)
        {
            ClearOutputs();
            StatusText = "Geen dataset met signalen beschikbaar.";
            return;
        }

        _analysisCalculation?.Cancel();
        using var calculation = new CancellationTokenSource();
        _analysisCalculation = calculation;
        IsBusy = true;
        StatusText = "Analyses worden berekend…";
        _failedAnalyses.Clear();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var operationProperties = new Dictionary<string, object?>
        {
            ["decoded_sample_bucket"] = TelemetryBuckets.Count(_dataset.DecodedSamples.Count),
            ["signal_bucket"] = TelemetryBuckets.Count(_dataset.SignalCount)
        };
        var operationId = _telemetryService.BeginCriticalOperation("analytics_recompute", operationProperties);
        try
        {
            _ = _telemetryService.TrackEventAsync("analytics_recompute_started", operationProperties);
            if (_usageDirty)
            {
                BusyLabel = "Joystickanalyse herberekenen...";
                await RunAnalysisSafelyAsync(BuildJoystickUsageAnalyticsAsync, "Gebruik");
                _usageDirty = false;
            }

            if (_trackingDirty)
            {
                BusyLabel = "Actuatoranalyse herberekenen...";
                await RunAnalysisSafelyAsync(BuildActuatorTrackingAnalyticsAsync, "Actuatorvolging");
                _trackingDirty = false;
            }

            if (_reactionDirty)
            {
                BusyLabel = "Vertragingsanalyse herberekenen...";
                await RunAnalysisSafelyAsync(BuildDelayAnalyticsAsync, "Latency");
                _reactionDirty = false;
            }

            if (_canDirty)
            {
                BusyLabel = "CAN-overzichten herberekenen...";
                await RunAnalysisSafelyAsync(BuildProfessionalCanAnalyticsAsync, "CAN");
                _canDirty = false;
            }
            BusyLabel = "Analyseoverzicht voorbereiden...";
            await RunAnalysisSafelyAsync(BuildOverviewAsync, "Overzicht");
            StatusText = _failedAnalyses.Count > 0
                ? $"Niet alle analyses berekend: {string.Join(", ", _failedAnalyses.Distinct())}. Bekijk de melding bij het onderdeel."
                : _dataset.Completeness == DatasetCompleteness.Partial
                ? "PARTIAL — analyses zijn gebaseerd op bewust onvolledig geaccepteerde data."
                : "Analyses bijgewerkt; beschikbaarheid en ontbrekende instellingen staan per onderdeel vermeld.";
            if (_failedAnalyses.Count == 0) _ = _telemetryService.TrackEventAsync("analytics_recompute_completed", new Dictionary<string, object?>
            {
                ["duration_ms"] = stopwatch.ElapsedMilliseconds,
                ["signal_bucket"] = TelemetryBuckets.Count(_dataset.SignalCount)
            });
        }
        catch (OperationCanceledException)
        {
            if (_dataset is not null) StatusText = "Instellingen gewijzigd; analyse wordt automatisch opnieuw berekend.";
        }
        catch (Exception ex)
        {
            _ = _telemetryService.TrackEventAsync("analytics_recompute_failed", new Dictionary<string, object?>
            {
                ["duration_ms"] = stopwatch.ElapsedMilliseconds,
                ["exception_type"] = ex.GetType().Name
            });
            throw;
        }
        finally
        {
            stopwatch.Stop();
            _telemetryService.CompleteCriticalOperation(operationId);
            IsBusy = false;
            if (ReferenceEquals(_analysisCalculation, calculation)) _analysisCalculation = null;
            if (_recomputeRequested)
            {
                _recomputeRequested = false;
                _ = RecomputeAsync();
            }
        }
    }

    private async Task RunAnalysisSafelyAsync(Func<Task> calculate, string name)
    {
        try { await calculate(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            _failedAnalyses.Add(name);
            var properties = new Dictionary<string, object?>
            {
                ["exception_type"] = ex.GetType().Name,
                ["analysis_stage"] = name
            };
            if (name == "Latency" && ex is ArgumentException invalidInput)
                properties["failure_reason"] = DelayInputFailure.GetTelemetryCode(invalidInput);
            _ = _telemetryService.TrackEventAsync("analytics_recompute_failed", properties);
            if (name == "Latency")
            {
                ClearReactionResults(ex.Message);
                _lastDelayResult = null; _exportFirstResponse = null; DelayMetrics.Clear();
                Add(DelayMetrics, "Niet berekend", ex.Message);
                DelayHistogramModel = EmptyPlot("Controleer signalen en tijdvenster");
                DelayOverlayModel = EmptyPlot("Controleer signalen en tijdvenster");
            }
            else if (name == "Actuatorvolging") { InvalidateTracking(); ActuatorSummary = ex.Message; }
            else if (name == "Overzicht")
            {
                OverviewStatus = "Analyseoverzicht niet beschikbaar: " + ex.Message;
                for (var index = 0; index < OverviewCards.Count; index++)
                    OverviewCards[index] = OverviewCards[index] with { State = "Kon niet berekenen", Answer = ex.Message };
            }
            else if (name == "Gebruik") { ClearUsageResults(); JoystickUsageStatus = ex.Message; }
            else { ProfessionalCanMetrics.Clear(); Add(ProfessionalCanMetrics, "Niet berekend", ex.Message); }
        }
    }

    private void ClearUsageResults()
    {
        ClearJoystickUsageSummary();
        _exportJoystick = null;
        StrokeCards.Clear(); JoystickUsageMetrics.Clear(); _currentJoystickFilteredPath = [];
        JoystickDeadzoneText = JoystickSaturationText = JoystickCoverageText = "—";
        JoystickDensityModel = EmptyPlot("Geen geldige analyse"); RadiusHistogramModel = EmptyPlot("Geen geldige analyse");
        TrajectoryModel = EmptyPlot("Geen geldige analyse"); StrokeDistributionModel = EmptyPlot("Geen geldige analyse");
    }

    private void InvalidateTracking()
    {
        _trackingDirty = true;
        _analysisRevision++;
        ActuatorMatrix.Clear();
        _exportTracking.Clear();
        ActuatorSummary = "Instellingen gewijzigd; herbereken actuatorvolging.";
        ActuatorLeftOverlayModel = EmptyPlot("Herbereken actuatorvolging");
        ActuatorRightOverlayModel = EmptyPlot("Herbereken actuatorvolging");
        ActuatorFrontOverlayModel = EmptyPlot("Herbereken actuatorvolging");
        ActuatorDelayHistogramModel = EmptyPlot("Kies setpoint en feedback per actuator");
    }

    private void RefreshProfessionalCanAnalyticsFromSettings()
    {
        _canDirty = true;
        if (_dataset is null || _loadingDataset)
        {
            return;
        }

        _analysisRevision++;
        _ = RecomputeAsync();
    }

    private async Task<T> ComputeAsync<T>(Func<T> calculate)
    {
        var dataset = _dataset;
        var revision = _analysisRevision;
        var token = _analysisCalculation?.Token ?? CancellationToken.None;
        var reader = dataset?.AcquireReadLease();
        T result;
        try { result = await Task.Run(() => { using (reader) { token.ThrowIfCancellationRequested(); return calculate(); } }); }
        catch (Exception) when (revision != _analysisRevision || !ReferenceEquals(dataset, _dataset))
        { throw new OperationCanceledException("Analysis settings changed."); }
        if (token.IsCancellationRequested || revision != _analysisRevision || !ReferenceEquals(dataset, _dataset))
            throw new OperationCanceledException("Analysis settings changed.");
        return result;
    }

    private void RecomputeJoystickWindowIfNeeded()
    {
        if (_suppressTimeWindowAutoRecompute || _dataset is null) return;
        InvalidateUsage(); InvalidateTracking();
        ClearReactionResults("Tijdvenster gewijzigd; bereken de analyse opnieuw.");
        _reactionDirty = true;
        _lastDelayResult = null; _exportFirstResponse = null; DelayMetrics.Clear();
        DelayOverlayModel = EmptyPlot("Tijdvenster gewijzigd; herbereken");
        DelayHistogramModel = EmptyPlot("Tijdvenster gewijzigd; herbereken");
        ScheduleAutomaticAnalysis();
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
            DelayOverlayModel = BuildReactionSourcePreview(overlayOptions: true);
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
        _detailCache.Clear(); _detailCacheDataset = null;
        JoystickModuleResult = null; ReactionModuleResult = null;
        ClearJoystickUsageSummary(); ClearReactionResults();
        StopJoystickPlayback();
        _lastDelayResult = null;
        _exportJoystick = null;
        _exportCan = null;
        StrokeCards.Clear();
        StrokeDistributionModel = EmptyPlot("Geen actuatorposities geselecteerd");
        JoystickDeadzoneText = JoystickSaturationText = JoystickCoverageText = "—";
        JoystickUsageStatus = "Laad een log om het gebruik te analyseren.";
        _currentJoystickFilteredPath = [];
        JoystickUsageMetrics.Clear();
        ActuatorMatrix.Clear();
        _exportTracking.Clear();
        ActuatorSummary = string.Empty;
        DelayMetrics.Clear();
        _exportFirstResponse = null;
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
        _usageDirty = true;
        await RecomputeAsync();
    }

    private async Task BuildJoystickUsageAnalyticsAsync()
    {
        JoystickModuleResult = null;
        ClearJoystickUsageSummary();
        _exportJoystick = null;
        StopJoystickPlayback();
        _currentJoystickFilteredPath = [];
        JoystickUsageMetrics.Clear();
        JoystickDeadzoneText = JoystickSaturationText = JoystickCoverageText = "—";
        JoystickDensityModel = EmptyPlot("Geen gezamenlijke joystickdata");
        TrajectoryModel = EmptyPlot("Geen traject beschikbaar");
        RadiusHistogramModel = EmptyPlot("Geen uitslagverdeling beschikbaar");
        var selectedSeries = new[] { SelectedJoystickXSignal, SelectedJoystickYSignal, SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal, SelectedCommandSignal, SelectedResponseSignal }
            .Select(label => TryGetSeries(label, out var series) ? series : null)
            .Where(series => series is not null).ToArray();
        var cancellation = _analysisCalculation?.Token ?? CancellationToken.None;
        var bounds = await ComputeAsync(() =>
        {
            foreach (var series in selectedSeries) series!.GetData(cancellation);
            var populated = selectedSeries.Where(s => s!.Time.Length > 0).ToArray();
            return (Start: populated.Length > 0 ? populated.Min(s => s!.Time[0]) : 0,
                End: populated.Length > 0 ? populated.Max(s => s!.Time[^1]) : 0);
        });
        _suppressTimeWindowAutoRecompute = true;
        try
        {
            JoystickAvailableStartSeconds = bounds.Start;
            JoystickAvailableEndSeconds = bounds.End;
            if (!UseJoystickTimeWindow)
            {
                JoystickTimeStartSeconds = JoystickAvailableStartSeconds;
                JoystickTimeEndSeconds = JoystickAvailableEndSeconds;
            }
        }
        finally { _suppressTimeWindowAutoRecompute = false; }
        await BuildStrokeUsageAsync();
        if (!double.IsFinite(DeadzoneThreshold) || !double.IsFinite(SaturationThreshold)
            || DeadzoneThreshold < 0 || SaturationThreshold > 1 || DeadzoneThreshold >= SaturationThreshold
            || !double.IsFinite(StrokeEdgePercent) || StrokeEdgePercent <= 0 || StrokeEdgePercent >= 50
            || HistogramBins < 10 || HistogramBins > 100
            || (UseJoystickTimeWindow && (!double.IsFinite(JoystickTimeStartSeconds) || !double.IsFinite(JoystickTimeEndSeconds) || JoystickTimeEndSeconds <= JoystickTimeStartSeconds)))
        {

            JoystickUsageStatus = "Controleer de instellingen: 0 ≤ deadzone < buitenrand ≤ 1; uiterste band > 0 en < 50%; 10–100 bins; einde na start.";
            return;
        }
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
        JoystickUsageResult result;
        var deadzone = DeadzoneThreshold; var saturation = SaturationThreshold;
        try
        {
            var profile = DetailProfile() with { Joysticks = [new("detail.joystick", "Geselecteerde joystick", sx.Identity, sy.Identity)
            {
                CalibrationConfirmed = UseJoystickCalibration, XCalibration = CreateJoystickXCalibration(), YCalibration = CreateJoystickYCalibration(),
                NeutralThreshold = deadzone, FullThreshold = saturation, HistogramBins = HistogramBins
            }] };
            var run = await AnalyzeDetailsAsync(profile, AnalysisModules.Joystick);
            JoystickModuleResult = run.Joysticks.Single();
            OnPropertyChanged(nameof(JoystickModuleResult));
            if (JoystickModuleResult.Value is not { } value) { JoystickUsageStatus = JoystickModuleResult.Explanation; return; }
            result = value;
        }
        catch (ArgumentException ex) { JoystickUsageStatus = ex.Message; return; }
        if (result.CoveredSeconds <= 0)
        {
            JoystickUsageStatus = "Geen gezamenlijke meetdekking in dit tijdvenster. Kies een ander venster of controleer de signalen.";
            return;
        }
        var densityModel = await ComputeAsync(() => BuildPointCloud(result.Points, deadzone, saturation));
        _exportJoystick = result;
        UpdateJoystickUsageSummary(result);
        JoystickDeadzoneText = UseJoystickCalibration ? UsagePercent(result.DeadzonePercent) : "Kalibratie nodig";
        JoystickSaturationText = UseJoystickCalibration ? UsagePercent(result.SaturationPercent) : "Kalibratie nodig";
        JoystickCoverageText = UsagePercent(100 * result.CoveredSeconds / result.WindowSeconds);
        JoystickUsageStatus = $"{result.CoveredSeconds:N1} s gezamenlijke meetdekking · {Math.Max(0, result.WindowSeconds - result.CoveredSeconds):N1} s zonder dekking overgeslagen. Percentages zijn tijdgewogen.";
        Add(JoystickUsageMetrics, "X-signaal", sx.Label);
        Add(JoystickUsageMetrics, "Y-signaal", sy.Label);
        Add(JoystickUsageMetrics, "Geldige intervallen", result.Points.Count.ToString("N0"));
        Add(JoystickUsageMetrics, "Normalisatie", UseJoystickCalibration
            ? $"X min/neutraal/max: {JoystickXMinimum:G} / {JoystickXNeutral:G} / {JoystickXMaximum:G}; Y: {JoystickYMinimum:G} / {JoystickYNeutral:G} / {JoystickYMaximum:G}."
            : "Relatief aan log-min/max; geen fysieke kalibratie.");
        Add(JoystickUsageMetrics, "Interpretatie", UseJoystickCalibration ? "Percentages ten opzichte van ingestelde kalibratie; controleer beide assen." : "Zonder bekende kalibratie geen neutraal- of maximale-uitslagpercentages.");
        Add(JoystickUsageMetrics, "Meetgaten", $"Gaten uit de import en intervallen groter dan min(5× mediaan, {UsageMaximumGapSeconds:G} s) tellen niet mee.");
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
        JoystickDensityModel = densityModel;
        var radiusHistogram = BuildUsageHistogram(result.RadiusDistribution, result.CoveredSeconds, "Joystickuitslag (genormaliseerd)");
        // A bound PlotView assigns annotation axes immediately when the model is
        // published. Complete the guides first, including for size-only redraws.
        AddRadiusGuides(radiusHistogram, DeadzoneThreshold, SaturationThreshold);
        RadiusHistogramModel = radiusHistogram;
        UpdateJoystickPlaybackPlots();
    }

    private SignalSeries? ResolveOptional(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        if (TryGetSeries(label, out var series))
        {
            if (CurrentProfile?.InvalidatedSignals.Contains(series.Identity) == true)
                throw new ArgumentException($"Signaaldefinitie gewijzigd; bevestig de koppeling en betekenis opnieuw: {label}");
            return series;
        }
        throw new ArgumentException($"Signaal niet beschikbaar: {label}");
    }

    private StrokeReference ReferenceFor(ActuatorRangeSettings settings) => new(settings.ManualCenter,
        settings.HalfRange ?? double.NaN, ResolveOptional(settings.CenterSignal), ResolveOptional(settings.HalfRangeSignal),
        ResolveOptional(settings.SetpointSignal), ResolveOptional(settings.RunSignal), settings.RunValue, UsageMaximumGapSeconds);

    private (double? Start, double? End) AnalysisWindow => UseJoystickTimeWindow
        ? (JoystickTimeStartSeconds, JoystickTimeEndSeconds) : (null, null);

    private async Task BuildStrokeUsageAsync()
    {
        StrokeCards.Clear();
        var model = new PlotModel { Title = "Tijd binnen beschikbaar regelbereik", TitleFontSize = 14, IsLegendVisible = true };
        model.Legends.Add(new OxyPlot.Legends.Legend { LegendPosition = OxyPlot.Legends.LegendPosition.TopRight });
        model.Axes.Add(Axis(AxisPosition.Bottom, "Positie binnen referentiebereik [%]", 0, 100));
        model.Axes.Add(Axis(AxisPosition.Left, "Aandeel beoordeelde tijd [%]", 0));
        var labels = new[] { SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal };
        var colors = new[] { "#2563EB", "#D97706", "#0D9488" };
        for (var i = 0; i < 3; i++)
        {
            var settings = ActuatorRanges[i];
            CalibratedStrokeResult? result = null;
            var note = "Kies feedback en een bekend nulpunt bij de bereikinstellingen.";
            try
            {
                if (TryGetSeries(labels[i], out var series))
                {
                    var run = await AnalyzeDetailsAsync(DetailProfile() with { Actuators = [DetailActuator(i)] }, AnalysisModules.Actuators);
                    var module = run.Summary.Actuators.Single();
                    var stroke = module.Value?.Stroke;
                    result = stroke?.Value;
                    note = stroke?.Explanation ?? module.Explanation;
                }
            }
            catch (ArgumentException ex) { note = ex.Message; }
            StrokeCards.Add(new(settings.Name, labels[i], colors[i], result, note));
            if (result is not { CoveredSeconds: > 0 }) continue;
            var line = new StairStepSeries { Title = settings.Name, Color = OxyColor.Parse(colors[i]), StrokeThickness = 2 };
            foreach (var bin in result.Distribution) line.Points.Add(new(bin.Start, bin.Seconds * 100 / result.CoveredSeconds));
            line.Points.Add(new(100, result.Distribution[^1].Seconds * 100 / result.CoveredSeconds));
            model.Series.Add(line);
        }
        StrokeDistributionModel = model.Series.Count > 0 ? model : EmptyPlot("Kies nulpunt en beschikbaar bereik per actuator");
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

    private static SignalSeries Windowed(SignalSeries source, (double? Start, double? End) window)
    {
        if (window.Start is not { } start || window.End is not { } end) return source;
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
            throw DelayInputFailure.Create(DelayInputFailureReason.InvalidTimeWindow);
        var indices = Enumerable.Range(0, source.Time.Length).Where(i => source.Time[i] >= start && source.Time[i] <= end).ToArray();
        return new SignalSeries(source.Identity, indices.Select(i => source.TimestampNanoseconds[i]).ToArray(),
            indices.Select(i => source.Value[i]).ToArray(), source.Label);
    }

    private async Task BuildActuatorTrackingAnalyticsAsync()
    {
        ActuatorMatrix.Clear();
        _exportTracking.Clear();
        var labels = new[] { SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal };
        var plots = new PlotModel[3];
        var errors = new string[3]; var coverage = new string[3];
        var notes = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var settings = ActuatorRanges[i];
            plots[i] = EmptyPlot("Kies setpoint en feedback"); errors[i] = coverage[i] = "—";
            try
            {
                if (!TryGetSeries(labels[i], out var feedback) || !TryGetSeries(settings.SetpointSignal, out var command)) continue;
                var window = AnalysisWindow;
                var run = await AnalyzeDetailsAsync(DetailProfile() with { Actuators = [DetailActuator(i)] }, AnalysisModules.Actuators);
                var module = run.Summary.Actuators.Single();
                var tracking = module.Value?.Tracking;
                var result = tracking?.Value;
                if (result is null) notes.Add(settings.Name + ": " + (tracking?.Explanation ?? module.Explanation));
                var calculated = await ComputeAsync(() =>
                {
                    var c = Windowed(command, window); var f = Windowed(feedback, window);
                    return (Command: BuildStairSeries("Setpoint", c.Time.Zip(c.Value, (t, v) => new TimeValuePoint(t, v)).ToArray(), OxyColors.SteelBlue, 1.5),
                        Feedback: BuildStairSeries("Feedback", f.Time.Zip(f.Value, (t, v) => new TimeValuePoint(t, v)).ToArray(), OxyColors.DarkOrange, 1.5));
                });
                _exportTracking.Add(new { settings.Name, Feedback = feedback.Label, Setpoint = command.Label, Module = tracking });
                errors[i] = result?.MeanAbsoluteError?.ToString("0.###", CultureInfo.CurrentCulture) ?? "—";
                coverage[i] = result is null ? "—" : $"{result.MovingSeconds:N1} s";
                var model = new PlotModel { Title = settings.Name + ": setpoint / feedback", IsLegendVisible = true };
                model.Legends.Add(new OxyPlot.Legends.Legend());
                model.Axes.Add(Axis(AxisPosition.Bottom, "Tijd [s]"));
                model.Axes.Add(Axis(AxisPosition.Left, "Positie [gedecodeerde eenheid]"));
                model.Series.Add(calculated.Command);
                model.Series.Add(calculated.Feedback);
                plots[i] = model;
            }
            catch (ArgumentException ex) { notes.Add(settings.Name + ": " + ex.Message); }
        }
        ActuatorMatrix.Add(new("Gem. absolute fout tijdens beweging [signaaleenheid]", errors[0], errors[1], errors[2]));
        ActuatorMatrix.Add(new("Beoordeelde bewegingstijd", coverage[0], coverage[1], coverage[2]));
        ActuatorLeftOverlayModel = plots[0]; ActuatorRightOverlayModel = plots[1]; ActuatorFrontOverlayModel = plots[2];
        ActuatorDelayHistogramModel = EmptyPlot("Voor reactietijd: kies deze signalen op Latency / Delay");
        ActuatorSummary = "Volgfout tijdens detecteerbare beweging volgens dezelfde methode als het overzicht. Gebruik dezelfde eenheid en hetzelfde nulpunt. " +
            "Tijdvenster en bedrijfsfilter volgen de gebruiksanalyse. Kies bovenaan een actuator voor reactietijd en volgen tijdens beweging. " + string.Join(" ", notes);
    }

    private void AddActuatorMatrixRow(string metric, double left, double right, double front)
        => ActuatorMatrix.Add(new ActuatorMetricRow(metric, F(left), F(right), F(front)));

    private static string DirText(double gain) => gain >= 0 ? "zelfde" : "omgekeerd";

    private static double UsageAnalyticsGap(SignalSeries series)
    {
        var intervals = series.Time.Zip(series.Time.Skip(1), (a, b) => b - a).Where(dt => dt > 0).Order().ToArray();
        return intervals.Length > 0 ? intervals[intervals.Length / 2] : 0;
    }

    private static string DelayNumber(double? value) => value?.ToString("0.######", CultureInfo.CurrentCulture) ?? "—";

    private async Task BuildDelayAnalyticsAsync()
    {
        ReactionModuleResult = null;
        DelayMetrics.Clear();
        _exportFirstResponse = null;
        ClearReactionResults();
        if (!TryGetSeries(SelectedCommandSignal, out var cmd) || !TryGetSeries(SelectedResponseSignal, out var rsp))
        {
            _lastDelayResult = null;
            ReactionStatus = "Kies het werkelijke setpoint en de gemeten positie. Bij onduidelijke signaalnamen is handmatige keuze nodig.";
            DelayHistogramModel = EmptyPlot("Kies setpoint en gemeten positie");
            DelayOverlayModel = EmptyPlot("Kies setpoint en gemeten positie");
            return;
        }
        if (ReferenceEquals(cmd, rsp)) throw new ArgumentException("Setpoint en positie moeten verschillende signalen zijn.");
        var window = AnalysisWindow; var gaps = MeasurementGapSelection.ForSeries(_dataset?.ImportReport, cmd, rsp);
        var options = DetailResponseOptions();
        var calculateCorrelation = CalculateCorrelation;
        var previewReport = _dataset?.ImportReport;
        var previewToken = _analysisCalculation?.Token ?? CancellationToken.None;
        var actuatorIndex = SelectedReactionActuator switch { "Links" => 0, "Rechts" => 1, "Voor" => 2, _ => -1 };
        var runSignal = actuatorIndex >= 0 ? DetailIdentity(ActuatorRanges[actuatorIndex].RunSignal) : null;
        var runValue = actuatorIndex >= 0 ? ActuatorRanges[actuatorIndex].RunValue : 1;
        var profile = DetailProfile() with { Actuators = [new("detail.reaction", "Geselecteerde actuator", cmd.Identity, rsp.Identity)
        {
            RunSignal = runSignal, RunValue = runValue, ResponseOptions = options, PositionReferenceConfirmed = PositionReferenceConfirmed
        }] };
        var detail = await AnalyzeDetailsAsync(profile, AnalysisModules.Actuators);
        ReactionModuleResult = detail.Summary.Actuators.Single();
        OnPropertyChanged(nameof(ReactionModuleResult));
        if (ReactionModuleResult.Status == ModuleStatus.Failed)
        {
            // Preserve the existing handled-failure telemetry without recomputing the measurements.
            DelayInputFailureReason? reason = ReactionModuleResult.ReasonCodes.FirstOrDefault() switch
            {
                "data_gap" => DelayInputFailureReason.DataGap,
                "duplicate_timestamp" => DelayInputFailureReason.DuplicateTimestamp,
                "non_monotonic_timestamp" => DelayInputFailureReason.NonMonotonicTimestamp,
                "non_finite_time" => DelayInputFailureReason.NonFiniteTime,
                "non_finite_value" => DelayInputFailureReason.NonFiniteValue,
                "invalid_time_window" => DelayInputFailureReason.InvalidTimeWindow,
                _ => null
            };
            throw reason is { } known ? DelayInputFailure.Create(known) : new ArgumentException(ReactionModuleResult.Explanation);
        }
        var measurements = ReactionModuleResult.Value;
        if (measurements?.SetpointChange.Value is not { } motion)
        {
            ReactionStatus = measurements?.SetpointChange.Explanation ?? ReactionModuleResult.Explanation;
            MotionTrackingStatus = measurements?.Tracking.Explanation ?? ReactionModuleResult.Explanation;
            return;
        }
        var positionRequest = measurements.PositionRequest.Value;
        var trackingResult = measurements.Tracking.Value;
        var referenceIssue = positionRequest is null ? measurements.PositionRequest.Explanation : null;
        var calculated = await ComputeAsync(() =>
        {
            var command = Windowed(cmd, window); var response = Windowed(rsp, window);
            DelayAnalysisResult? correlation = null;
            string? note = null;
            if (calculateCorrelation)
            {
                if (command.Time.Length > 0 && response.Time.Length > 0 && gaps.Any(gap =>
                    gap.StartSeconds < Math.Min(command.Time[^1], response.Time[^1]) &&
                    gap.EndSeconds > Math.Max(command.Time[0], response.Time[0])))
                    note = "Niet berekend: de selectie overlapt een meetgat. Kies een aaneengesloten tijdvenster voor vormvergelijking.";
                else
                {
                    try { correlation = _analyticsService.AnalyzeDelay(command, response, options.MaxDelaySeconds, .5, 20_000); }
                    catch (InvalidOperationException ex) { note = ex.Message; }
                }
            }
            var preview = PrepareReactionSourcePreview(cmd, rsp, motion, window, previewReport, previewToken);
            return (Motion: motion, PositionRequest: positionRequest, Tracking: trackingResult, Correlation: correlation, Note: note, Command: cmd, Response: rsp, UnitIssue: referenceIssue, Preview: preview);
        });
        _exportMotionResponse = calculated.Motion;
        _exportPositionRequest = calculated.PositionRequest;
        _reactionCommand = calculated.Command; _reactionFeedback = calculated.Response;
        _lastDelayResult = calculated.Correlation;
        _reactionUnitMismatchNote = calculated.UnitIssue;
        SetReactionSourcePreview(calculated.Preview);
        if (calculated.Tracking is { } tracking) PublishMotionTracking(tracking);
        else MotionTrackingStatus = measurements.Tracking.Explanation;
        RefreshReactionSummary();
        var result = calculated.Motion;
        Add(DelayMetrics, "Setpoint", result.CommandSignalLabel);
        Add(DelayMetrics, "Gemeten positie", result.ResponseSignalLabel);
        Add(DelayMetrics, "Setpointwijziging → reactie", "Vanaf wijziging van de setpointwaarde; reactie in de richting van die wijziging.");
        Add(DelayMetrics, "Positievraag → reactie", "Vanaf een setpointupdate die een nieuwe richting vraagt buiten de marge rond de gemeten positie. Gelijke eenheid en nulreferentie vereist.");
        Add(DelayMetrics, "Bewegingsgrens [signaaleenheid]", result.ResponseThreshold.ToString("G5", CultureInfo.CurrentCulture));
        Add(DelayMetrics, "Commandogrens [signaaleenheid]", result.CommandThreshold.ToString("G5", CultureInfo.CurrentCulture));
        Add(DelayMetrics, "Positiemarge [signaaleenheid]", calculated.PositionRequest?.PositionTolerance?.ToString("G5", CultureInfo.CurrentCulture) ?? "—");
        Add(DelayMetrics, "Setpoint: gemeten / gevonden (alle categorieën)", $"{result.AllStatistics.MatchedEventCount} / {result.Events.Count}");
        Add(DelayMetrics, "Positievraag: gemeten / gevonden (alle categorieën)", calculated.PositionRequest is { } positionResult
            ? $"{positionResult.AllStatistics.MatchedEventCount} / {positionResult.Events.Count}" : calculated.UnitIssue ?? "Niet berekend");
        Add(DelayMetrics, "Maximale zoektijd [s]", options.MaxDelaySeconds.ToString("G4", CultureInfo.CurrentCulture));
        Add(DelayMetrics, "Correlatie", calculateCorrelation
            ? calculated.Note ?? "Alleen vormvergelijking; geen gemiddelde reactietijd."
            : "Niet berekend; optioneel onder Geavanceerd.");
        if (_lastDelayResult is not null)
        {
            Add(DelayMetrics, "Vormverschuiving [ms]", (_lastDelayResult.BestLagSeconds * 1000).ToString("0.#", CultureInfo.CurrentCulture));
            Add(DelayMetrics, "Correlatiekwaliteit", _lastDelayResult.BestCorrelation.ToString("0.###", CultureInfo.CurrentCulture));
        }
        RefreshDelayOverlayFromCache();
    }

    private void AutoDetectJoystickSignals()
    {
        SelectedJoystickXSignal = SelectedJoystickYSignal = SelectedJoystickYawSignal = null;
        if (_dataset is null) return;
        var joystick = AvailableSignals.Where(label => Has(label, ["joy", "joystick"]))
            .Select(label => _dataset.SignalSeriesByLabel[label]).ToArray();
        var xs = joystick.Where(s => Has(s.Label, ["pos_x", "joystick_x", "_x", ".x", " axisx"])).ToArray();
        var ys = joystick.Where(s => Has(s.Label, ["pos_y", "joystick_y", "_y", ".y", " axisy"])).ToArray();
        var pairs = (from x in xs from y in ys
            where x.Identity.Channel == y.Identity.Channel && x.Identity.FrameId == y.Identity.FrameId
                && x.Identity.MessageName == y.Identity.MessageName && x.Identity.FrameFormat == y.Identity.FrameFormat
                && x.Identity.IsExtended == y.Identity.IsExtended
            select (X: x, Y: y)).ToArray();
        if (pairs.Length == 1)
        {
            SelectedJoystickXSignal = pairs[0].X.Label;
            SelectedJoystickYSignal = pairs[0].Y.Label;
        }
        else if (xs.Length == 1 && ys.Length == 0) SelectedJoystickXSignal = xs[0].Label;
        else if (ys.Length == 1 && xs.Length == 0) SelectedJoystickYSignal = ys[0].Label;
        var yaw = joystick.Where(s => Has(s.Label, ["yaw", "twist", "rz", "rot"])).ToArray();
        if (yaw.Length == 1) SelectedJoystickYawSignal = yaw[0].Label;
    }

    private void AutoDetectButterflySignals()
    {
        AutoDetectJoystickSignals();
        if (AvailableSignals.Count == 0) return;
        var labels = AvailableSignals.ToList();
        SelectedActuatorLeftSignal = BestActuator(labels, ["left", "links"]);
        SelectedActuatorRightSignal = BestActuator(labels, ["right", "rechts"]);
        SelectedActuatorFrontSignal = BestActuator(labels, ["front", "voor"]);
        var feedbacks = new[] { SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal };
        for (var i = 0; i < feedbacks.Length; i++)
        {
            if (!TryGetSeries(feedbacks[i], out var feedback) || _dataset is null) continue;
            var identity = feedback.Identity;
            var siblings = _dataset.SignalLabels.Where(label => _dataset.SignalSeriesByLabel.TryGetValue(label, out var series)
                && series.Identity.Channel == identity.Channel && series.Identity.FrameId == identity.FrameId
                && series.Identity.MessageName == identity.MessageName && label != feedback.Label).ToArray();
            string? Unique(string[] tokens)
            {
                var matches = siblings.Where(label => Has(_dataset.SignalSeriesByLabel[label].Identity.SignalName, tokens)).ToArray();
                return matches.Length == 1 ? matches[0] : null;
            }
            ActuatorRanges[i].CenterSignal = Unique(["calibr", "offset", "zeropos", "center"]);
            ActuatorRanges[i].SetpointSignal = Unique(["setpoint", "target", "command", "realpos"]);
        }
    }

    private void AutoDetectDelaySignals()
    {
        var feedbacks = new[] { SelectedActuatorLeftSignal, SelectedActuatorRightSignal, SelectedActuatorFrontSignal };
        for (var i = 0; i < 3; i++)
        {
            if (string.IsNullOrWhiteSpace(feedbacks[i]) || string.IsNullOrWhiteSpace(ActuatorRanges[i].SetpointSignal)) continue;
            _syncingReactionPair = true;
            try
            {
                SelectedReactionActuator = ReactionActuators[i];
                SelectedCommandSignal = ActuatorRanges[i].SetpointSignal;
                SelectedResponseSignal = feedbacks[i];
            }
            finally { _syncingReactionPair = false; }
            return;
        }
        SelectedReactionActuator = "Vrije signaalkeuze";
        var commands = AvailableSignals.Where(v => Has(v, ["realpos", "cmd", "command", "target", "setpoint", "request", "desired"])
            && !Has(v, ["force", "current", "temperature", "status", "error", "heartbeat"])).ToArray();
        var responses = AvailableSignals.Where(v => Has(v, ["actualpos", "actual", "feedback", "measured", "position", "encoder"])
            && !Has(v, ["force", "current", "temperature", "status", "error", "heartbeat"])).ToArray();
        SelectedCommandSignal = commands.Length == 1 ? commands[0] : null;
        SelectedResponseSignal = responses.Length == 1 ? responses[0] : null;
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

        InvalidateTracking();
        InvalidateDelay();
        _ = RecomputeUsageAsync();
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
        var previousFinite = false;
        for (var i = 0; i < values.Count; i++)
        {
            var finite = double.IsFinite(values[i]);
            // Keep every coverage transition even when regular points are thinned for display.
            if (!finite)
            {
                if (previousFinite) series.Points.Add(DataPoint.Undefined);
                previousFinite = false;
                continue;
            }
            if (i % stride != 0 && previousFinite && i != values.Count - 1 && double.IsFinite(values[i + 1])) continue;
            var t = startTime + (i * binWidthSeconds);
            series.Points.Add(new DataPoint(t, values[i]));
            previousFinite = true;
        }

        model.Series.Add(series);
        return model;
    }

    private static PlotModel BuildTopIdBarPlot(IReadOnlyList<CanDetailStream> topByCount, long totalFrames)
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
            categoryAxis.Labels.Add(CanDisplayKey(shown[i]));
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
        model.Legends.Add(new OxyPlot.Legends.Legend { LegendPosition = OxyPlot.Legends.LegendPosition.TopLeft });
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
        var ranked = labels.Where(l => sideTokens.Any(t => l.Contains(t, StringComparison.OrdinalIgnoreCase)) && (l.Contains("pos", StringComparison.OrdinalIgnoreCase) || l.Contains("position", StringComparison.OrdinalIgnoreCase)) && !new[] { "target", "setpoint", "command", "desired", "offset", "calibr", "realpos" }.Any(t => l.Contains(t, StringComparison.OrdinalIgnoreCase))).Select(l => new { Label = l, Score = ActuatorScore(l, sideTokens) }).OrderByDescending(v => v.Score).ThenBy(v => v.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
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

    private sealed record ChangeEvent(double Time, double Value, int Direction, double Magnitude);
    private sealed record DelayVisualMatch(ChangeEvent Command, ChangeEvent Response, double DelaySeconds);

}
