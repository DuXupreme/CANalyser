using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
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
    private readonly IJoystickAnalyticsService _analyticsService;
    private readonly DispatcherTimer _joystickPlaybackTimer;
    private CanDataset? _dataset;
    private DelayAnalysisResult? _lastDelayResult;
    private bool _suppressTimeWindowAutoRecompute;
    private bool _suppressPlaybackAutoRefresh;
    private DateTime _lastPlaybackTickUtc;
    private IReadOnlyList<TimeNormalizedPoint> _currentJoystickFilteredPath = [];
    private double _currentJoystickDeadzone;
    private double _currentJoystickSaturation;

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
    [ObservableProperty] private int _canTimeBinMilliseconds = 100;
    [ObservableProperty] private double _delaySearchRangeSeconds = 1.5;
    [ObservableProperty] private string _statusText = "Laad eerst log + DBC en open daarna dit tabblad.";

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

    public JoystickAnalyticsViewModel(IJoystickAnalyticsService analyticsService)
    {
        _analyticsService = analyticsService;
        RecomputeCommand = new RelayCommand(Recompute);
        AutoDetectJoystickPairCommand = new RelayCommand(AutoDetectButterflySignals);
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

    public ObservableCollection<string> AvailableSignals { get; } = [];
    public ObservableCollection<MetricRow> JoystickUsageMetrics { get; } = [];
    public ObservableCollection<MetricRow> ActuatorMetrics { get; } = [];
    public ObservableCollection<MetricRow> DelayMetrics { get; } = [];
    public ObservableCollection<MetricRow> ProfessionalCanMetrics { get; } = [];
    public ObservableCollection<CanTopIdRow> ProfessionalTopIdRows { get; } = [];
    public ObservableCollection<CanCycleTimingRow> ProfessionalCycleRows { get; } = [];
    public IRelayCommand RecomputeCommand { get; }
    public IRelayCommand AutoDetectJoystickPairCommand { get; }
    public IRelayCommand AutoDetectActuatorPairCommand { get; }
    public IRelayCommand AutoDetectDelaySignalsCommand { get; }
    public IRelayCommand ResetJoystickTimeWindowCommand { get; }
    public IRelayCommand ToggleJoystickPlaybackCommand { get; }
    public IRelayCommand ResetJoystickPlaybackCommand { get; }

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
        if (value < 0.1)
        {
            JoystickPlaybackSpeed = 0.1;
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
        _dataset = dataset;
        AvailableSignals.Clear();
        foreach (var label in dataset.SignalLabels) AvailableSignals.Add(label);
        AutoDetectButterflySignals();
        AutoDetectDelaySignals();
        Recompute();
    }

    private void Recompute()
    {
        if (_dataset is null || _dataset.SignalSeriesByLabel.Count == 0)
        {
            ClearOutputs();
            StatusText = "Geen dataset met signalen beschikbaar.";
            return;
        }

        BuildJoystickUsageAnalytics();
        BuildActuatorTrackingAnalytics();
        BuildDelayAnalytics();
        BuildProfessionalCanAnalytics();
        StatusText = "Analyses bijgewerkt.";
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
        if (_suppressTimeWindowAutoRecompute || _dataset is null)
        {
            return;
        }

        if (!UseJoystickTimeWindow)
        {
            Recompute();
            return;
        }

        if (JoystickTimeEndSeconds > JoystickTimeStartSeconds)
        {
            Recompute();
        }
    }

    private void UpdateJoystickPlaybackPlots()
    {
        if (_suppressPlaybackAutoRefresh)
        {
            return;
        }

        if (_currentJoystickFilteredPath.Count == 0)
        {
            JoystickDensityModel = EmptyPlot("Joystick puntenwolk");
            TrajectoryModel = EmptyPlot("Joystick traject");
            return;
        }

        if (!IsJoystickPlaybackEnabled)
        {
            var allPoints = _currentJoystickFilteredPath.Select(static p => new NormalizedPoint(p.X, p.Y)).ToArray();
            var current = _currentJoystickFilteredPath[^1];
            var marker = new NormalizedPoint(current.X, current.Y);
            JoystickDensityModel = BuildPointCloud(allPoints, _currentJoystickDeadzone, _currentJoystickSaturation, marker);
            TrajectoryModel = BuildTrajectory(allPoints, _currentJoystickDeadzone, _currentJoystickSaturation, marker);
            return;
        }

        var start = _currentJoystickFilteredPath[0].Time;
        var end = _currentJoystickFilteredPath[^1].Time;
        var playbackTime = Math.Clamp(JoystickPlaybackPositionSeconds, start, end);
        if (Math.Abs(playbackTime - JoystickPlaybackPositionSeconds) > 1e-9)
        {
            _suppressPlaybackAutoRefresh = true;
            try
            {
                JoystickPlaybackPositionSeconds = playbackTime;
            }
            finally
            {
                _suppressPlaybackAutoRefresh = false;
            }
        }

        var visible = _currentJoystickFilteredPath.Where(point => point.Time <= playbackTime).ToArray();
        if (visible.Length == 0)
        {
            visible = [_currentJoystickFilteredPath[0]];
        }

        var points = visible.Select(static p => new NormalizedPoint(p.X, p.Y)).ToArray();
        var currentPoint = visible[^1];
        var currentMarker = new NormalizedPoint(currentPoint.X, currentPoint.Y);
        JoystickDensityModel = BuildPointCloud(points, _currentJoystickDeadzone, _currentJoystickSaturation, currentMarker);
        TrajectoryModel = BuildTrajectory(points, _currentJoystickDeadzone, _currentJoystickSaturation, currentMarker);
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
        _currentJoystickFilteredPath = [];
        JoystickUsageMetrics.Clear();
        ActuatorMetrics.Clear();
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
        DelayHistogramModel = EmptyPlot("Delay histogram");
        DelayOverlayModel = EmptyPlot("Command/response overlay");
        CanFrameRateModel = EmptyPlot("Frames/s over tijd");
        CanBusLoadModel = EmptyPlot("Bus load [%] over tijd");
        CanTopIdPlotModel = EmptyPlot("Top CAN-IDs");
    }

    private void BuildJoystickUsageAnalytics()
    {
        StopJoystickPlayback();
        JoystickUsageMetrics.Clear();
        if (!TryGetSeries(SelectedJoystickXSignal, out var sx) || !TryGetSeries(SelectedJoystickYSignal, out var sy))
        {
            JoystickDensityModel = EmptyPlot("Joystick puntenwolk");
            TrajectoryModel = EmptyPlot("Joystick traject");
            RadiusHistogramModel = EmptyPlot("Joystick radius histogram");
            return;
        }

        var timedPath = BuildTimedNormalizedPath(sx, sy);
        if (timedPath.Count == 0)
        {
            JoystickDensityModel = EmptyPlot("Joystick puntenwolk");
            TrajectoryModel = EmptyPlot("Joystick traject");
            RadiusHistogramModel = EmptyPlot("Joystick radius histogram");
            return;
        }

        _suppressTimeWindowAutoRecompute = true;
        try
        {
            JoystickAvailableStartSeconds = timedPath[0].Time;
            JoystickAvailableEndSeconds = timedPath[^1].Time;
            if (JoystickTimeEndSeconds <= JoystickTimeStartSeconds)
            {
                JoystickTimeStartSeconds = JoystickAvailableStartSeconds;
                JoystickTimeEndSeconds = JoystickAvailableEndSeconds;
            }
        }
        finally
        {
            _suppressTimeWindowAutoRecompute = false;
        }

        var filteredPath = FilterTimedPath(timedPath);
        if (filteredPath.Count == 0)
        {
            filteredPath = timedPath;
            _suppressTimeWindowAutoRecompute = true;
            try
            {
                UseJoystickTimeWindow = false;
                JoystickTimeStartSeconds = JoystickAvailableStartSeconds;
                JoystickTimeEndSeconds = JoystickAvailableEndSeconds;
            }
            finally
            {
                _suppressTimeWindowAutoRecompute = false;
            }
        }

        var filteredX = filteredPath.Select(static p => p.X).ToArray();
        var filteredY = filteredPath.Select(static p => p.Y).ToArray();
        var filteredTime = filteredPath.Select(static p => (float)p.Time).ToArray();
        var filteredXSeries = new SignalSeries(sx.Label, filteredTime, filteredX.Select(static v => (float)v).ToArray());
        var filteredYSeries = new SignalSeries(sy.Label, filteredTime, filteredY.Select(static v => (float)v).ToArray());

        var result = _analyticsService.AnalyzePair(
            filteredXSeries,
            filteredYSeries,
            Math.Max(0, DeadzoneThreshold),
            Math.Max(0, SaturationThreshold),
            Math.Clamp(HistogramBins, 10, 100),
            24000);
        var s = result.Statistics;
        Add(JoystickUsageMetrics, "Joystick X", result.XSignalLabel);
        Add(JoystickUsageMetrics, "Joystick Y", result.YSignalLabel);
        Add(JoystickUsageMetrics, "Analysetijd [s]",
            UseJoystickTimeWindow
                ? $"{F(JoystickTimeStartSeconds)} .. {F(JoystickTimeEndSeconds)}"
                : $"{F(JoystickAvailableStartSeconds)} .. {F(JoystickAvailableEndSeconds)}");
        Add(JoystickUsageMetrics, "Samples", s.SampleCount.ToString("N0", CultureInfo.CurrentCulture));
        Add(JoystickUsageMetrics, "Deadzone usage [%]", F(s.DeadzonePercent));
        Add(JoystickUsageMetrics, "Saturation usage [%]", F(s.SaturationPercent));
        Add(JoystickUsageMetrics, "Gebruikte X-range [%]", F(s.UsedXRangePercent));
        Add(JoystickUsageMetrics, "Gebruikte Y-range [%]", F(s.UsedYRangePercent));
        Add(JoystickUsageMetrics, "Bias/center-offset X", F(s.BiasX));
        Add(JoystickUsageMetrics, "Bias/center-offset Y", F(s.BiasY));
        Add(JoystickUsageMetrics, "Quadrant I [%]", F(s.Quadrant1Percent));
        Add(JoystickUsageMetrics, "Quadrant II [%]", F(s.Quadrant2Percent));
        Add(JoystickUsageMetrics, "Quadrant III [%]", F(s.Quadrant3Percent));
        Add(JoystickUsageMetrics, "Quadrant IV [%]", F(s.Quadrant4Percent));
        Add(JoystickUsageMetrics, "P95 radius", F(s.Percentile95Radius));
        Add(JoystickUsageMetrics, "Aanbevolen gevoeligheid", SensitivityAdvice(s));

        _currentJoystickFilteredPath = filteredPath;
        _currentJoystickDeadzone = Math.Max(0, DeadzoneThreshold);
        _currentJoystickSaturation = Math.Max(0, SaturationThreshold);
        _suppressPlaybackAutoRefresh = true;
        try
        {
            JoystickPlaybackStartSeconds = filteredPath[0].Time;
            JoystickPlaybackEndSeconds = filteredPath[^1].Time;
            if (JoystickPlaybackPositionSeconds < JoystickPlaybackStartSeconds || JoystickPlaybackPositionSeconds > JoystickPlaybackEndSeconds)
            {
                JoystickPlaybackPositionSeconds = JoystickPlaybackEndSeconds;
            }
        }
        finally
        {
            _suppressPlaybackAutoRefresh = false;
        }

        UpdateJoystickPlaybackPlots();
        RadiusHistogramModel = BuildHistogram("Joystick radius histogram", result.RadiusHistogram, "Radius", OxyColor.Parse("#F28E2B"));
    }

    private void BuildActuatorTrackingAnalytics()
    {
        ActuatorMetrics.Clear();
        if (!TryGetSeries(SelectedJoystickXSignal, out var jx) || !TryGetSeries(SelectedJoystickYSignal, out var jy) || !TryGetSeries(SelectedJoystickYawSignal, out var jyaw) ||
            !TryGetSeries(SelectedActuatorLeftSignal, out var al) || !TryGetSeries(SelectedActuatorRightSignal, out var ar) || !TryGetSeries(SelectedActuatorFrontSignal, out var af))
        {
            ActuatorLeftOverlayModel = EmptyPlot("Left actuator");
            ActuatorRightOverlayModel = EmptyPlot("Right actuator");
            ActuatorFrontOverlayModel = EmptyPlot("Front actuator");
            ActuatorDelayHistogramModel = EmptyPlot("Delay histogram vlinder");
            return;
        }

        var r = _analyticsService.AnalyzeButterflyKinematics(jx, jy, jyaw, al, ar, af, Math.Max(0, DeadzoneThreshold), Math.Max(0.05, DelaySearchRangeSeconds), Math.Clamp(HistogramBins, 10, 90), 12000);
        Add(ActuatorMetrics, "Wat analyseert dit", "Joystick X/Y/Yaw wordt omgerekend naar expected Left/Right/Front en vergeleken met feedback.");
        Add(ActuatorMetrics, "Mapping", "Left=-Y-Yaw | Right=-Y+Yaw | Front=+X");
        AddAxisRows("Left", r.LeftTracking, r.LeftDelay);
        AddAxisRows("Right", r.RightTracking, r.RightDelay);
        AddAxisRows("Front", r.FrontTracking, r.FrontDelay);
        var total = r.LeftDelay.MatchedEventCount + r.RightDelay.MatchedEventCount + r.FrontDelay.MatchedEventCount;
        Add(ActuatorMetrics, "Totaal matched events", total.ToString("N0", CultureInfo.CurrentCulture));
        Add(ActuatorMetrics, "Delay avg totaal [s]", FN(WeightedMean(r.LeftDelay, r.RightDelay, r.FrontDelay)));
        Add(ActuatorMetrics, "Delay min totaal [s]", FN(MinDelay(r.LeftDelay, r.RightDelay, r.FrontDelay)));
        Add(ActuatorMetrics, "Delay max totaal [s]", FN(MaxDelay(r.LeftDelay, r.RightDelay, r.FrontDelay)));
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

    private void AddAxisRows(string name, AxisTrackingStatistics t, DelayEventStatistics d)
    {
        Add(ActuatorMetrics, $"{name} corr", F(t.Correlation));
        Add(ActuatorMetrics, $"{name} gain", F(t.Gain));
        Add(ActuatorMetrics, $"{name} richting", t.Gain >= 0 ? "zelfde richting" : "omgekeerde richting");
        Add(ActuatorMetrics, $"{name} RMSE(norm)", F(t.NormalizedRmse));
        Add(ActuatorMetrics, $"{name} delay avg [s]", FN(d.MeanDelaySeconds));
        Add(ActuatorMetrics, $"{name} delay min [s]", FN(d.MinimumDelaySeconds));
        Add(ActuatorMetrics, $"{name} delay max [s]", FN(d.MaximumDelaySeconds));
        Add(ActuatorMetrics, $"{name} events", d.MatchedEventCount.ToString("N0", CultureInfo.CurrentCulture));
    }

    private void BuildDelayAnalytics()
    {
        DelayMetrics.Clear();
        if (!TryGetSeries(SelectedCommandSignal, out var cmd) || !TryGetSeries(SelectedResponseSignal, out var rsp))
        {
            _lastDelayResult = null;
            DelayHistogramModel = EmptyPlot("Delay histogram");
            DelayOverlayModel = EmptyPlot("Command/response overlay");
            return;
        }

        var r = _analyticsService.AnalyzeDelay(cmd, rsp, Math.Max(0.05, DelaySearchRangeSeconds), 2800, 0.5, 9000);
        _lastDelayResult = r;
        Add(DelayMetrics, "Command", r.CommandSignalLabel);
        Add(DelayMetrics, "Response", r.ResponseSignalLabel);
        Add(DelayMetrics, "Gem delay [s]", FN(r.MeanEventDelaySeconds));
        Add(DelayMetrics, "Min delay [s]", FN(r.MinimumEventDelaySeconds));
        Add(DelayMetrics, "Max delay [s]", FN(r.MaximumEventDelaySeconds));
        Add(DelayMetrics, "P95 delay [s]", FN(r.Percentile95EventDelaySeconds));
        Add(DelayMetrics, "Matched events", r.MatchedDelayEvents.ToString("N0", CultureInfo.CurrentCulture));
        Add(DelayMetrics, "Rising events", r.RisingDelay.MatchedEventCount.ToString("N0", CultureInfo.CurrentCulture));
        Add(DelayMetrics, "Rising avg [s]", FN(r.RisingDelay.MeanDelaySeconds));
        Add(DelayMetrics, "Falling events", r.FallingDelay.MatchedEventCount.ToString("N0", CultureInfo.CurrentCulture));
        Add(DelayMetrics, "Falling avg [s]", FN(r.FallingDelay.MeanDelaySeconds));
        DelayHistogramModel = BuildHistogram("Delay histogram (event-based)", r.DelayHistogram, "Delta-t [s]", OxyColor.Parse("#59A14F"));
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
        var orderedFrames = IsTimeSorted(frames) ? frames : frames.OrderBy(static frame => frame.TimeSeconds).ToArray();
        var start = orderedFrames[0].TimeSeconds;
        var end = orderedFrames[^1].TimeSeconds;
        var duration = Math.Max(1e-9, end - start);
        var bitrate = Math.Max(50, CanBitrateKbps) * 1000.0;
        var binWidthSeconds = Math.Clamp(CanTimeBinMilliseconds / 1000.0, 0.05, 5.0);
        var binCount = Math.Clamp((int)Math.Ceiling(duration / binWidthSeconds) + 1, 1, 200000);
        var frameRateBins = new double[binCount];
        var busBitsBins = new double[binCount];
        var perId = new Dictionary<uint, CanIdAccumulator>(256);
        var errorFrames = 0;
        var remoteFrames = 0;
        var totalBits = 0.0;
        foreach (var frame in orderedFrames)
        {
            var bin = Math.Clamp((int)((frame.TimeSeconds - start) / binWidthSeconds), 0, binCount - 1);
            frameRateBins[bin] += 1;

            var estimatedBits = EstimateFrameBits(frame);
            busBitsBins[bin] += estimatedBits;
            totalBits += estimatedBits;

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

            if (!perId.TryGetValue(frame.Id, out var idStats))
            {
                idStats = new CanIdAccumulator(frame.Id);
                perId.Add(frame.Id, idStats);
            }

            idStats.Observe(frame);
        }

        var avgFrameRate = orderedFrames.Count / duration;
        var peakFrameRate = frameRateBins.Max() / binWidthSeconds;
        var avgBusLoad = ((totalBits / duration) / bitrate) * 100.0;
        var peakBusLoad = (busBitsBins.Max() / binWidthSeconds / bitrate) * 100.0;
        var uniqueIds = perId.Count;
        var extendedCount = orderedFrames.Count(static frame => frame.IsExtended);
        Add(ProfessionalCanMetrics, "Duur log [s]", F(duration));
        Add(ProfessionalCanMetrics, "Totale frames", orderedFrames.Count.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Unieke IDs", uniqueIds.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Extended frames", extendedCount.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Error frames (geschat)", errorFrames.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Remote/RTR frames", remoteFrames.ToString("N0", CultureInfo.CurrentCulture));
        Add(ProfessionalCanMetrics, "Gem frames/s", F(avgFrameRate));
        Add(ProfessionalCanMetrics, "Piek frames/s", F(peakFrameRate));
        Add(ProfessionalCanMetrics, "Gem bus load [%]", F(avgBusLoad));
        Add(ProfessionalCanMetrics, "Piek bus load [%]", F(peakBusLoad));
        Add(ProfessionalCanMetrics, "Bitrate [kbps] (instelling)", F(Math.Max(50, CanBitrateKbps)));
        Add(ProfessionalCanMetrics, "Venster [ms] (instelling)", Math.Clamp(CanTimeBinMilliseconds, 50, 5000).ToString(CultureInfo.CurrentCulture));

        var topByCount = perId.Values
            .OrderByDescending(static stats => stats.Count)
            .ThenBy(static stats => stats.FrameId)
            .Take(30)
            .ToArray();
        foreach (var item in topByCount)
        {
            var share = item.Count * 100.0 / Math.Max(1, orderedFrames.Count);
            ProfessionalTopIdRows.Add(new CanTopIdRow(
                $"0x{item.FrameId:X}",
                item.Count.ToString("N0", CultureInfo.CurrentCulture),
                F(share),
                F(item.MeanDlc),
                item.ExtendedCount > 0 ? "Ext/Std mix" : "Std"));
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
                $"0x{item.FrameId:X}",
                item.CycleSamples.ToString("N0", CultureInfo.CurrentCulture),
                F(item.AverageCycleMs),
                F(item.JitterMs),
                F(item.MinCycleMs),
                F(item.MaxCycleMs)));
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
            busBitsBins.Select(bits => (bits / binWidthSeconds / bitrate) * 100.0).ToArray(),
            "Bus load [%]",
            OxyColor.Parse("#E15759"),
            true);
        CanTopIdPlotModel = BuildTopIdBarPlot(topByCount, orderedFrames.Count);
    }

    private void AutoDetectButterflySignals()
    {
        if (AvailableSignals.Count == 0) return;
        var labels = AvailableSignals.ToList();
        var joy = labels.Where(v => Has(v, ["joy", "joystick"])).ToList();
        var act = labels.Where(v => Has(v, ["act", "actuator", "actual", "realpos", "feedback", "position"])).ToList();
        SelectedJoystickXSignal = Pick(joy, ["pos_x", "joystick_x", "_x", ".x", " axisx"]) ?? Pick(labels, ["pos_x", "joystick_x", "_x", ".x", " axisx"]);
        SelectedJoystickYSignal = Pick(joy, ["pos_y", "joystick_y", "_y", ".y", " axisy"]) ?? Pick(labels, ["pos_y", "joystick_y", "_y", ".y", " axisy"]);
        SelectedJoystickYawSignal = Pick(joy, ["yaw", "twist", "rz", "rot"]) ?? Pick(labels, ["yaw", "twist", "rz", "rot"]);
        SelectedActuatorLeftSignal = BestActuator(labels, ["left", "links"]) ?? Pick(act, ["left", "links"]) ?? Pick(labels, ["left", "links"]);
        SelectedActuatorRightSignal = BestActuator(labels, ["right", "rechts"]) ?? Pick(act, ["right", "rechts"]) ?? Pick(labels, ["right", "rechts"]);
        SelectedActuatorFrontSignal = BestActuator(labels, ["front", "voor"]) ?? Pick(act, ["front", "voor"]) ?? Pick(labels, ["front", "voor"]);
        SelectedJoystickXSignal ??= labels.FirstOrDefault();
        SelectedJoystickYSignal ??= labels.Skip(1).FirstOrDefault() ?? labels.FirstOrDefault();
        SelectedJoystickYawSignal ??= labels.Skip(2).FirstOrDefault() ?? SelectedJoystickYSignal;
        SelectedActuatorLeftSignal ??= act.FirstOrDefault() ?? labels.FirstOrDefault();
        SelectedActuatorRightSignal ??= act.Skip(1).FirstOrDefault() ?? SelectedActuatorLeftSignal;
        SelectedActuatorFrontSignal ??= act.Skip(2).FirstOrDefault() ?? SelectedActuatorRightSignal;
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

        Recompute();
    }

    private bool TryGetSeries(string? label, out SignalSeries series)
    {
        series = default!;
        return _dataset is not null && !string.IsNullOrWhiteSpace(label) && _dataset.SignalSeriesByLabel.TryGetValue(label, out series);
    }

    private List<TimeNormalizedPoint> BuildTimedNormalizedPath(SignalSeries xSeries, SignalSeries ySeries)
    {
        if (xSeries.Time.Length == 0 || ySeries.Time.Length == 0 || xSeries.Value.Length == 0 || ySeries.Value.Length == 0)
        {
            return [];
        }

        var useXAsBase = xSeries.Time.Length <= ySeries.Time.Length;
        var baseTime = useXAsBase ? xSeries.Time : ySeries.Time;
        var baseValue = useXAsBase ? xSeries.Value : ySeries.Value;
        var otherTime = useXAsBase ? ySeries.Time : xSeries.Time;
        var otherValue = useXAsBase ? ySeries.Value : xSeries.Value;

        var rawX = new double[baseTime.Length];
        var rawY = new double[baseTime.Length];
        var time = new double[baseTime.Length];

        for (var i = 0; i < baseTime.Length; i++)
        {
            var t = baseTime[i];
            time[i] = t;
            if (useXAsBase)
            {
                rawX[i] = baseValue[i];
                rawY[i] = Interpolate(otherTime, otherValue, t);
            }
            else
            {
                rawX[i] = Interpolate(otherTime, otherValue, t);
                rawY[i] = baseValue[i];
            }
        }

        var sortedX = rawX.OrderBy(static v => v).ToArray();
        var sortedY = rawY.OrderBy(static v => v).ToArray();
        var centerX = Percentile(sortedX, 0.50);
        var centerY = Percentile(sortedY, 0.50);
        var scale = Math.Max(
            1e-9,
            Math.Max(
                Percentile(sortedX, 0.99) - Percentile(sortedX, 0.01),
                Percentile(sortedY, 0.99) - Percentile(sortedY, 0.01)) / 2.0);

        var points = new List<TimeNormalizedPoint>(time.Length);
        for (var i = 0; i < time.Length; i++)
        {
            var nx = Math.Clamp((rawX[i] - centerX) / scale, -1.2, 1.2);
            var ny = Math.Clamp((rawY[i] - centerY) / scale, -1.2, 1.2);
            points.Add(new TimeNormalizedPoint(time[i], nx, ny));
        }

        return points;
    }

    private List<TimeNormalizedPoint> FilterTimedPath(IReadOnlyList<TimeNormalizedPoint> path)
    {
        if (path.Count == 0 || !UseJoystickTimeWindow)
        {
            return path.ToList();
        }

        var start = Math.Max(JoystickAvailableStartSeconds, Math.Min(JoystickTimeStartSeconds, JoystickTimeEndSeconds));
        var end = Math.Min(JoystickAvailableEndSeconds, Math.Max(JoystickTimeStartSeconds, JoystickTimeEndSeconds));
        return path.Where(p => p.Time >= start && p.Time <= end).ToList();
    }

    private static double Interpolate(float[] time, float[] value, double sampleTime)
    {
        if (time.Length == 0 || value.Length == 0)
        {
            return 0;
        }

        if (sampleTime <= time[0])
        {
            return value[0];
        }

        if (sampleTime >= time[^1])
        {
            return value[^1];
        }

        var hi = UpperBound(time, (float)sampleTime);
        var i1 = Math.Clamp(hi, 1, time.Length - 1);
        var i0 = i1 - 1;
        var dt = time[i1] - time[i0];
        if (Math.Abs(dt) <= 1e-12)
        {
            return value[i1];
        }

        var p = (sampleTime - time[i0]) / dt;
        return value[i0] + ((value[i1] - value[i0]) * p);
    }

    private static int UpperBound(float[] values, float threshold)
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
        IReadOnlyList<NormalizedPoint> points,
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
        var model = new PlotModel
        {
            Title = "Genormaliseerde joystick puntenwolk",
            IsLegendVisible = false
        };
        model.Axes.Add(Axis(AxisPosition.Bottom, "Joystick X (genormaliseerd)", min, max));
        model.Axes.Add(Axis(AxisPosition.Left, "Joystick Y (genormaliseerd)", min, max));

        var scatter = new ScatterSeries
        {
            MarkerType = MarkerType.Circle,
            MarkerSize = 1.7,
            MarkerFill = OxyColor.Parse("#E22A2A"),
            MarkerStroke = OxyColor.Parse("#E22A2A"),
            MarkerStrokeThickness = 0.4
        };

        var stride = Math.Max(1, (int)Math.Ceiling(points.Count / 16000.0));
        for (var i = 0; i < points.Count; i += stride)
        {
            scatter.Points.Add(new ScatterPoint(points[i].X, points[i].Y));
        }

        model.Series.Add(scatter);
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
        var model = new PlotModel { Title = "Joystick traject (genormaliseerd)", IsLegendVisible = true };
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

    private static double EstimateFrameBits(RawCanFrame frame)
    {
        var payloadBits = frame.Dlc * 8.0;
        var baseBits = frame.IsExtended ? 67.0 + payloadBits : 47.0 + payloadBits;
        var stuffingMargin = baseBits * 0.20;
        const double intermissionBits = 3.0;
        return baseBits + stuffingMargin + intermissionBits;
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

        var model = new PlotModel { Title = "Top CAN-IDs op frame-aandeel" };
        var xAxis = Axis(AxisPosition.Bottom, "Top-ID rank");
        xAxis.Minimum = 0.5;
        xAxis.Maximum = topByCount.Count + 0.5;
        xAxis.MajorStep = 1;
        var yAxis = Axis(AxisPosition.Left, "Aandeel [%]", 0);
        model.Axes.Add(xAxis);
        model.Axes.Add(yAxis);

        var stems = new StemSeries
        {
            Title = "Aandeel",
            Color = OxyColor.Parse("#59A14F"),
            StrokeThickness = 2.0
        };
        for (var i = 0; i < topByCount.Count; i++)
        {
            var share = (topByCount[i].Count * 100.0) / Math.Max(1, totalFrames);
            var x = i + 1;
            stems.Points.Add(new DataPoint(x, share));
            model.Annotations.Add(new TextAnnotation
            {
                Text = $"0x{topByCount[i].FrameId:X}",
                TextPosition = new DataPoint(x, share),
                TextHorizontalAlignment = HorizontalAlignment.Center,
                TextVerticalAlignment = VerticalAlignment.Top,
                Stroke = OxyColors.Undefined,
                FontSize = 9,
                TextColor = OxyColor.Parse("#333333")
            });
        }

        model.Series.Add(stems);
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
    private static string F(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
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
        var ranked = labels.Select(l => new { Label = l, Score = ActuatorScore(l, sideTokens) }).OrderByDescending(v => v.Score).ThenBy(v => v.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
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

    private sealed class CanIdAccumulator(uint frameId)
    {
        private double _lastTimeSeconds = double.NaN;
        private double _cycleM2Ms;
        private double _cycleMeanMs;

        public uint FrameId { get; } = frameId;
        public int Count { get; private set; }
        public int ExtendedCount { get; private set; }
        public int DlcSum { get; private set; }
        public int CycleSamples { get; private set; }
        public double MinCycleMs { get; private set; } = double.PositiveInfinity;
        public double MaxCycleMs { get; private set; } = double.NegativeInfinity;

        public double MeanDlc => Count <= 0 ? 0 : DlcSum / (double)Count;
        public double AverageCycleMs => CycleSamples <= 0 ? 0 : _cycleMeanMs;
        public double JitterMs => CycleSamples <= 1 ? 0 : Math.Sqrt(_cycleM2Ms / (CycleSamples - 1));

        public void Observe(RawCanFrame frame)
        {
            Count++;
            DlcSum += frame.Dlc;
            if (frame.IsExtended)
            {
                ExtendedCount++;
            }

            if (!double.IsNaN(_lastTimeSeconds))
            {
                var cycleMs = Math.Max(0, (frame.TimeSeconds - _lastTimeSeconds) * 1000.0);
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
    private sealed record TimeNormalizedPoint(double Time, double X, double Y);
}
