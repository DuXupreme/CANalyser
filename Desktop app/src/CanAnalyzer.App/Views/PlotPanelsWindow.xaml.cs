using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Utilities;
using CanAnalyzer.App.Services;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Wpf;

namespace CanAnalyzer.App.Views;

public partial class PlotPanelsWindow : Window, INotifyPropertyChanged
{
    private readonly XAxisSyncService _xAxisSyncService = new();
    private readonly LatestWorkQueue<VisualBuildRequest, OxyPlot.Series.Series[][]> _visualBuilds;
    private readonly CancellationTokenSource _windowLifetime = new();
    private readonly List<Axis> _navigationAxes = [];
    private readonly DateTimeOffset? _startTimeUtc;
    private DateTime _lastCursorUpdateUtc = DateTime.MinValue;
    private double? _cursorTime;
    private double? _flagATime;
    private double? _flagBTime;
    private int _subplotHeight;
    private readonly DisplayLimits _displayLimits;
    private bool _stepPlot;
    private bool _markersOnly;
    private bool _showLegend = true;
    private bool _linkXAxisAcrossPanels = true;
    private bool _linkYAxisAcrossPanels = true;
    private bool _showCursorValueLabels = true;
    private bool _suppressVisualRefresh;
    private bool _restoringView;
    private bool _isClosed;
    private bool _isBusy;
    private string _plotStatus = string.Empty;
    private PlotPanelModel? _activeCursorPanel;

    public PlotPanelsWindow(
        IReadOnlyList<PlotPanelModel> panels,
        PlotViewOptions renderedOptions,
        DisplayLimits displayLimits,
        bool linkXAxisAcrossPanels,
        bool linkYAxisAcrossPanels,
        DateTimeOffset? startTimeUtc)
    {
        InitializeComponent();
        var history = new PlotInteractionHistory(this, () => Panels.Select(p => p.PlotModel));
        Panels.CollectionChanged += (_, _) => history.Clear();
        _subplotHeight = Math.Clamp(renderedOptions.SubplotHeight, 160, 1300);
        _displayLimits = displayLimits;
        _visualBuilds = new(
            (request, cancellation) => Task.Run(() => BuildVisualSeries(request, cancellation), cancellation),
            PublishVisualSeries,
            running =>
            {
                if (_isClosed) return;
                IsBusy = running;
                if (running) PlotStatus = "Grafiek bijwerken...";
            },
            error =>
            {
                if (!_isClosed) PlotStatus = $"Grafiek bijwerken mislukt: {error.Message}";
            });
        _displayLimits.PropertyChanged += OnDisplayLimitsChanged;
        _linkXAxisAcrossPanels = linkXAxisAcrossPanels;
        _linkYAxisAcrossPanels = linkYAxisAcrossPanels;
        _startTimeUtc = startTimeUtc;

        foreach (var panel in panels)
        {
            Panels.Add(panel);
        }

        InferInitialVisualOptions();
        DataContext = this;
        Loaded += OnLoaded;
        Closed += OnClosed;
        BindNavigationHandlers();
        // The user can change shared limits while the panels are being built in the background.
        if (renderedOptions.UseDownsampling != UseDownsampling ||
            (UseDownsampling && renderedOptions.MaxPointsPerTrace != MaxPointsPerTrace))
            ApplyVisualOptionsPreservingView();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PlotPanelModel> Panels { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public string PlotStatus
    {
        get => _plotStatus;
        private set => SetField(ref _plotStatus, value);
    }

    public int SubplotHeight
    {
        get => _subplotHeight;
        set => SetField(ref _subplotHeight, Math.Clamp(value, 160, 1300));
    }

    public int MaxPointsPerTrace
    {
        get => _displayLimits.MaxPointsPerTrace;
        set => _displayLimits.MaxPointsPerTrace = value;
    }

    public bool UseDownsampling
    {
        get => _displayLimits.UseDownsampling;
        set => _displayLimits.UseDownsampling = value;
    }

    private void OnDisplayLimitsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DisplayLimits.MaxPointsPerTrace) or nameof(DisplayLimits.UseDownsampling))
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(DisplayLimits.UseDownsampling) && UseDownsampling && !_visualBuilds.IsRunning)
            {
                foreach (var panel in Panels)
                    ViewportSampling.ResumeDownsampling(panel.PlotModel, MaxPointsPerTrace);
                PlotStatus = string.Empty;
                return;
            }
            if (UseDownsampling || e.PropertyName == nameof(DisplayLimits.UseDownsampling))
                ApplyVisualOptionsPreservingView();
        }
    }

    public bool StepPlot
    {
        get => _stepPlot;
        set
        {
            if (SetField(ref _stepPlot, value))
            {
                ApplyVisualOptionsPreservingView();
            }
        }
    }

    public bool MarkersOnly
    {
        get => _markersOnly;
        set
        {
            if (SetField(ref _markersOnly, value))
            {
                ApplyVisualOptionsPreservingView();
            }
        }
    }

    public bool ShowLegend
    {
        get => _showLegend;
        set
        {
            if (SetField(ref _showLegend, value))
            {
                foreach (var panel in Panels)
                {
                    panel.PlotModel.IsLegendVisible = value;
                    panel.PlotModel.InvalidatePlot(false);
                }
            }
        }
    }

    public bool ShowCursorValueLabels
    {
        get => _showCursorValueLabels;
        set
        {
            if (SetField(ref _showCursorValueLabels, value))
            {
                RefreshCursorAnnotations();
            }
        }
    }

    public bool LinkXAxisAcrossPanels
    {
        get => _linkXAxisAcrossPanels;
        set
        {
            if (SetField(ref _linkXAxisAcrossPanels, value))
            {
                ApplyAxisSyncConfiguration();
            }
        }
    }

    public bool LinkYAxisAcrossPanels
    {
        get => _linkYAxisAcrossPanels;
        set
        {
            if (SetField(ref _linkYAxisAcrossPanels, value))
            {
                ApplyAxisSyncConfiguration();
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _activeCursorPanel ??= Panels.FirstOrDefault();
        _restoringView = true;
        try
        {
            ApplyAxisSyncConfiguration();
            _xAxisSyncService.Bind(Panels.Select(panel => panel.PlotModel));
        }
        finally { _restoringView = false; }
        RefreshCursorAnnotations();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _windowLifetime.Cancel();
        _windowLifetime.Dispose();
        _displayLimits.PropertyChanged -= OnDisplayLimitsChanged;
        UnbindNavigationHandlers();
        _xAxisSyncService.Bind([]);
    }

    private void BindNavigationHandlers()
    {
        UnbindNavigationHandlers();
        foreach (var axis in Panels.SelectMany(panel => panel.PlotModel.Axes))
        {
            axis.AxisChanged += OnNavigationAxisChanged;
            _navigationAxes.Add(axis);
        }
    }

    private void UnbindNavigationHandlers()
    {
        foreach (var axis in _navigationAxes) axis.AxisChanged -= OnNavigationAxisChanged;
        _navigationAxes.Clear();
    }

    private void OnNavigationAxisChanged(object? sender, AxisChangedEventArgs e)
    {
        if (!_restoringView && !_isClosed && !UseDownsampling)
            UseDownsampling = true;
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not PlotView plotView || plotView.DataContext is not PlotPanelModel panel)
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed ||
            e.RightButton == MouseButtonState.Pressed ||
            e.MiddleButton == MouseButtonState.Pressed)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastCursorUpdateUtc).TotalMilliseconds < 70)
        {
            return;
        }

        if (!TryGetTimeAtMouse(plotView, e.GetPosition(plotView), out var time))
        {
            return;
        }

        _lastCursorUpdateUtc = now;
        SetCursorAt(time, panel);
    }

    private void OnPlotPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not PlotView plotView || plotView.DataContext is not PlotPanelModel panel)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left && e.ClickCount >= 2)
        {
            ResetAllPlots();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (TryToggleLegendSeries(plotView, e.GetPosition(plotView)))
        {
            RefreshCursorAnnotations();
            e.Handled = true;
            return;
        }

        if (!TryGetTimeAtMouse(plotView, e.GetPosition(plotView), out var time))
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SetFlagAAt(time, panel);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            SetFlagBAt(time, panel);
            e.Handled = true;
            return;
        }

        SetCursorAt(time, panel);
    }

    private void OnSetFlagAFromCursorClick(object sender, RoutedEventArgs e)
    {
        if (_cursorTime.HasValue)
        {
            SetFlagAAt(_cursorTime.Value, panel: null);
        }
    }

    private void OnSetFlagBFromCursorClick(object sender, RoutedEventArgs e)
    {
        if (_cursorTime.HasValue)
        {
            SetFlagBAt(_cursorTime.Value, panel: null);
        }
    }

    private void OnJumpFlagBToNextChangeClick(object sender, RoutedEventArgs e)
    {
        if (!_flagATime.HasValue)
        {
            CursorValueText.Text = "Signaalwaarde: zet eerst Flag A.";
            return;
        }

        var next = FindNextChangeTime(_flagATime.Value);
        if (!next.HasValue)
        {
            CursorValueText.Text = "Signaalwaarde: geen volgende verandering gevonden.";
            return;
        }

        SetFlagBAt(next.Value, panel: null);
        CursorValueText.Text = $"Signaalwaarde: Flag B op t={next.Value:G17}s";
    }

    private void OnClearFlagsClick(object sender, RoutedEventArgs e)
    {
        _flagATime = null;
        _flagBTime = null;
        UpdateCursorInfo();
        RefreshCursorAnnotations();
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        ResetAllPlots();
    }

    private void SetCursorAt(double requestedTime, PlotPanelModel? panel)
    {
        _activeCursorPanel = panel ?? _activeCursorPanel;
        var snappedTime = SnapTimeToNearestData(panel, requestedTime, out var nearestLabel, out var nearestValue);
        var newValueText = nearestLabel is null
            ? "Signaalwaarde: -"
            : $"Signaalwaarde: {ShortSignalLabel(nearestLabel)} = {nearestValue?.ToString("G17", CultureInfo.InvariantCulture)}";

        if (_cursorTime.HasValue &&
            Math.Abs(_cursorTime.Value - snappedTime) < 1e-6 &&
            string.Equals(CursorValueText.Text, newValueText, StringComparison.Ordinal))
        {
            return;
        }

        _cursorTime = snappedTime;
        CursorValueText.Text = newValueText;
        UpdateCursorInfo();
        RefreshCursorAnnotations();
    }

    private void SetFlagAAt(double requestedTime, PlotPanelModel? panel)
    {
        _activeCursorPanel = panel ?? _activeCursorPanel;
        _flagATime = SnapTimeToNearestData(panel, requestedTime, out _, out _);
        _cursorTime = _flagATime;
        UpdateCursorInfo();
        RefreshCursorAnnotations();
    }

    private void SetFlagBAt(double requestedTime, PlotPanelModel? panel)
    {
        _activeCursorPanel = panel ?? _activeCursorPanel;
        _flagBTime = SnapTimeToNearestData(panel, requestedTime, out _, out _);
        _cursorTime = _flagBTime;
        UpdateCursorInfo();
        RefreshCursorAnnotations();
    }

    private void ResetAllPlots()
    {
        foreach (var panel in Panels)
        {
            ResetPlotToDefaultView(panel.PlotModel);
            panel.PlotModel.InvalidatePlot(false);
        }

        ApplyAxisSyncConfiguration();
        _xAxisSyncService.Bind(Panels.Select(panel => panel.PlotModel));
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

    private void UpdateCursorInfo()
    {
        var cursorText = _cursorTime.HasValue ? $"{_cursorTime.Value:G17}s" : "-";
        var aText = _flagATime.HasValue ? $"{_flagATime.Value:G17}s" : "-";
        var bText = _flagBTime.HasValue ? $"{_flagBTime.Value:G17}s" : "-";
        var dtText = _flagATime.HasValue && _flagBTime.HasValue
            ? $"{Math.Abs(_flagBTime.Value - _flagATime.Value):G17}s"
            : "-";
        var absoluteText = FormatAbsoluteLocal(_cursorTime);
        CursorInfoText.Text = absoluteText is null
            ? $"Cursor: {cursorText} | A: {aText} | B: {bText} | dt: {dtText}"
            : $"Cursor: {cursorText} | Datum/tijd lokaal: {absoluteText}\n" +
              $"A: {FormatFlagTime(_flagATime, aText)} | B: {FormatFlagTime(_flagBTime, bText)} | dt: {dtText}";
    }

    private string FormatFlagTime(double? timeSeconds, string relativeText)
    {
        var absoluteText = FormatAbsoluteLocal(timeSeconds);
        return absoluteText is null ? relativeText : $"{relativeText} ({absoluteText})";
    }

    private string? FormatAbsoluteLocal(double? timeSeconds)
    {
        if (_startTimeUtc is not { } startTimeUtc ||
            !timeSeconds.HasValue ||
            !MeasurementTimestamp.TrySecondsToNanoseconds(timeSeconds.Value, out var nanoseconds))
        {
            return null;
        }

        return MeasurementTimestamp.FormatLocal(startTimeUtc, nanoseconds);
    }

    private void RefreshCursorAnnotations()
    {
        foreach (var panel in Panels)
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

            if (_cursorTime.HasValue)
            {
                var cursorTime = _cursorTime.Value;
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

                if (ShowCursorValueLabels)
                {
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
                            FontWeight = OxyPlot.FontWeights.Bold,
                            FontSize = 10
                        });

                        drawnLabels++;
                    }
                }
            }

            if (_flagATime.HasValue)
            {
                model.Annotations.Add(new LineAnnotation
                {
                    Tag = "cursor-a",
                    Type = LineAnnotationType.Vertical,
                    X = _flagATime.Value,
                    Color = OxyColors.OrangeRed,
                    LineStyle = LineStyle.Solid,
                    StrokeThickness = 1.5,
                    Text = "A",
                    TextColor = OxyColors.OrangeRed
                });
            }

            if (_flagBTime.HasValue)
            {
                model.Annotations.Add(new LineAnnotation
                {
                    Tag = "cursor-b",
                    Type = LineAnnotationType.Vertical,
                    X = _flagBTime.Value,
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

    private double? FindNextChangeTime(double fromTime)
    {
        double? best = null;
        const double eps = 1e-9;

        foreach (var panel in Panels)
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

        IEnumerable<PlotPanelModel> panels = panel is null ? Panels : [panel];
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

    private static bool TryGetTimeAtMouse(PlotView plotView, System.Windows.Point point, out double time)
    {
        time = 0;
        var model = plotView.ActualModel;
        if (model is null)
        {
            return false;
        }

        var xAxis = model.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);
        if (xAxis is null)
        {
            return false;
        }

        try
        {
            time = xAxis.InverseTransform(point.X);
            return !double.IsNaN(time) && !double.IsInfinity(time);
        }
        catch
        {
            return false;
        }
    }

    private void InferInitialVisualOptions()
    {
        _suppressVisualRefresh = true;
        try
        {
            var firstPanel = Panels.FirstOrDefault();
            if (firstPanel is null)
            {
                return;
            }

            _showLegend = firstPanel.PlotModel.IsLegendVisible;

            var firstSeries = firstPanel.PlotModel.Series.FirstOrDefault();
            _markersOnly = firstSeries is ScatterSeries;
            _stepPlot = firstSeries is StairStepSeries;

            OnPropertyChanged(nameof(ShowLegend));
            OnPropertyChanged(nameof(MarkersOnly));
            OnPropertyChanged(nameof(StepPlot));
            OnPropertyChanged(nameof(SubplotHeight));
            OnPropertyChanged(nameof(MaxPointsPerTrace));
            OnPropertyChanged(nameof(UseDownsampling));
            OnPropertyChanged(nameof(LinkXAxisAcrossPanels));
            OnPropertyChanged(nameof(LinkYAxisAcrossPanels));
            OnPropertyChanged(nameof(ShowCursorValueLabels));
        }
        finally
        {
            _suppressVisualRefresh = false;
        }
    }

    private void ApplyVisualOptionsPreservingView()
    {
        if (_isClosed || _suppressVisualRefresh || Panels.Count == 0) return;

        // Capture UI state now; workers only read these source buffers and their own options.
        var options = CaptureVisualOptions();
        if (options.UseDownsampling)
            foreach (var panel in Panels) ViewportSampling.ResumeDownsampling(panel.PlotModel, MaxPointsPerTrace);
        var panels = Panels.Select(panel => new PanelBuildRequest(panel.SeriesData.ToArray(),
            CaptureViewport(panel.PlotModel))).ToArray();
        _ = _visualBuilds.RequestAsync(new(options, panels, _windowLifetime.Token));
    }

    private PlotViewOptions CaptureVisualOptions() => new()
    {
        UseDownsampling = UseDownsampling,
        MaxPointsPerTrace = MaxPointsPerTrace,
        MarkersOnly = MarkersOnly,
        StepPlot = StepPlot,
        ShowLegend = ShowLegend
    };

    private static PlotViewport? CaptureViewport(PlotModel model)
    {
        var axis = model.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);
        if (axis is null) return null;
        var range = CaptureAxisRange(axis);
        return double.IsFinite(range.Minimum) && double.IsFinite(range.Maximum) && range.Maximum > range.Minimum
            ? new PlotViewport(range.Minimum, range.Maximum) : null;
    }

    private static OxyPlot.Series.Series[][] BuildVisualSeries(VisualBuildRequest request, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, request.WindowLifetime);
        var token = linked.Token;
        var result = new OxyPlot.Series.Series[request.Panels.Length][];
        for (var i = 0; i < request.Panels.Length; i++)
        {
            var panel = request.Panels[i];
            result[i] = new OxyPlot.Series.Series[panel.Sources.Length];
            for (var j = 0; j < panel.Sources.Length; j++)
            {
                token.ThrowIfCancellationRequested();
                result[i][j] = PlotSeriesRenderer.Build(panel.Sources[j], request.Options, panel.Viewport);
            }
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private void PublishVisualSeries(VisualBuildRequest request, OxyPlot.Series.Series[][] rendered)
    {
        if (_isClosed || rendered.Length != Panels.Count) return;
        // Capture at publication, so a pan, zoom or legend toggle during the build wins.
        var snapshots = CapturePanelSnapshots();
        _restoringView = true;
        _xAxisSyncService.Bind([]);
        try
        {
            for (var i = 0; i < Panels.Count; i++)
            {
                var model = Panels[i].PlotModel;
                model.IsLegendVisible = ShowLegend;
                model.Series.Clear();
                foreach (var series in rendered[i]) model.Series.Add(series);
            }
            RestorePanelSnapshots(snapshots);
            // These same models already carry the current linked ranges. Rebinding must not
            // turn a pending automatic reset back into an explicit range from stale extents.
            _xAxisSyncService.Configure(syncXAxis: false, syncYAxis: false);
            _xAxisSyncService.Bind(Panels.Select(panel => panel.PlotModel));
            ApplyAxisSyncConfiguration();
            for (var i = 0; i < Panels.Count; i++)
            {
                var panel = Panels[i];
                // Preparation belongs to the worker-created series. Attach only binds events.
                ViewportSampling.Attach(panel.PlotModel, request.Panels[i].Sources,
                    request.Options.UseDownsampling, request.Options.MaxPointsPerTrace, request.Panels[i].Viewport);
                panel.PlotModel.InvalidatePlot(true);
            }
        }
        finally { _restoringView = false; }
        RefreshCursorAnnotations();
        PlotStatus = string.Empty;
    }

    private void ApplyAxisSyncConfiguration()
    {
        _xAxisSyncService.Configure(syncXAxis: LinkXAxisAcrossPanels, syncYAxis: LinkYAxisAcrossPanels);
    }

    // Retained for the series construction regression check; live refreshes use the worker above.
    private OxyPlot.Series.Series BuildSeries(RenderedSeriesData series, double? visibleMinimum, double? visibleMaximum) =>
        PlotSeriesRenderer.Build(series, CaptureVisualOptions(),
            visibleMinimum is { } minimum && visibleMaximum is { } maximum &&
            double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum
                ? new PlotViewport(minimum, maximum) : null);

    private static AxisRange CaptureAxisRange(Axis axis) =>
        axis is ViewportAxis viewportAxis
            ? new(viewportAxis.VisibleMinimum, viewportAxis.VisibleMaximum, !viewportAxis.HasUserRange)
            : new(axis.ActualMinimum, axis.ActualMaximum, false);

    private List<PanelViewSnapshot> CapturePanelSnapshots()
    {
        var snapshots = new List<PanelViewSnapshot>(Panels.Count);
        foreach (var panel in Panels)
        {
            var axisRanges = new Dictionary<string, AxisRange>(StringComparer.Ordinal);
            foreach (var axis in panel.PlotModel.Axes)
            {
                axisRanges[GetAxisId(axis)] = CaptureAxisRange(axis);
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
        var count = Math.Min(snapshots.Count, Panels.Count);
        for (var i = 0; i < count; i++)
        {
            var panel = Panels[i];
            var snapshot = snapshots[i];

            foreach (var axis in panel.PlotModel.Axes)
            {
                if (!snapshot.AxisRanges.TryGetValue(GetAxisId(axis), out var range))
                {
                    continue;
                }

                if (range.IsAutomatic)
                {
                    axis.Reset();
                    continue;
                }

                if (!double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum) || range.Maximum <= range.Minimum)
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

    private static bool TryToggleLegendSeries(PlotView plotView, System.Windows.Point point)
    {
        var model = plotView.ActualModel;
        if (model is null)
        {
            return false;
        }

        var screenPoint = new ScreenPoint(point.X, point.Y);
        var inLegend = model.Legends.Any(legend => legend.LegendArea.Contains(screenPoint));
        if (!inLegend)
        {
            return false;
        }

        var hitArgs = new HitTestArguments(screenPoint, 6);
        var hits = model.HitTest(hitArgs).ToList();
        var hitSeries = hits
            .SelectMany(hit => new OxyPlot.Series.Series?[]
            {
                hit.Element as OxyPlot.Series.Series,
                hit.Item as OxyPlot.Series.Series
            })
            .FirstOrDefault(series => series is not null);
        if (hitSeries is null)
        {
            return false;
        }

        hitSeries.IsVisible = !hitSeries.IsVisible;
        model.InvalidatePlot(false);
        return true;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record AxisRange(double Minimum, double Maximum, bool IsAutomatic);

    private sealed record PanelBuildRequest(RenderedSeriesData[] Sources, PlotViewport? Viewport);

    private sealed record VisualBuildRequest(PlotViewOptions Options, PanelBuildRequest[] Panels, CancellationToken WindowLifetime);

    private sealed record PanelViewSnapshot(
        IReadOnlyDictionary<string, AxisRange> AxisRanges,
        IReadOnlyDictionary<string, bool> SeriesVisibility);
}
