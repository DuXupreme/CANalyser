using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Analysis;
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
    private DateTime _lastCursorUpdateUtc = DateTime.MinValue;
    private double? _cursorTime;
    private double? _flagATime;
    private double? _flagBTime;
    private int _subplotHeight;
    private int _maxPointsPerTrace = 4000;
    private bool _useDownsampling = true;
    private bool _stepPlot;
    private bool _markersOnly;
    private bool _showLegend = true;
    private bool _linkYAxisAcrossPanels = true;
    private bool _showCursorValueLabels = true;
    private bool _suppressVisualRefresh;
    private PlotPanelModel? _activeCursorPanel;

    public PlotPanelsWindow(
        IReadOnlyList<PlotPanelModel> panels,
        int subplotHeight,
        int maxPointsPerTrace,
        bool useDownsampling)
    {
        InitializeComponent();
        _subplotHeight = Math.Clamp(subplotHeight, 160, 1300);
        _maxPointsPerTrace = Math.Clamp(maxPointsPerTrace, 200, 200_000);
        _useDownsampling = useDownsampling;

        foreach (var panel in panels)
        {
            Panels.Add(panel);
        }

        InferInitialVisualOptions();
        DataContext = this;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PlotPanelModel> Panels { get; } = [];

    public int SubplotHeight
    {
        get => _subplotHeight;
        set => SetField(ref _subplotHeight, Math.Clamp(value, 160, 1300));
    }

    public int MaxPointsPerTrace
    {
        get => _maxPointsPerTrace;
        set
        {
            if (SetField(ref _maxPointsPerTrace, Math.Clamp(value, 200, 200_000)))
            {
                ApplyVisualOptionsPreservingView();
            }
        }
    }

    public bool UseDownsampling
    {
        get => _useDownsampling;
        set
        {
            if (!value)
            {
                var oversized = Panels.SelectMany(panel => panel.SeriesData.Select(series =>
                {
                    var xAxis = panel.PlotModel.Axes.FirstOrDefault(static axis => axis.Position == AxisPosition.Bottom);
                    return CountVisible(series.Time, xAxis?.ActualMinimum, xAxis?.ActualMaximum);
                })).FirstOrDefault(static count => count > 200_000);
                if (oversized > 200_000)
                {
                    MessageBox.Show(this,
                        $"Volledige resolutie bevat {oversized:N0} zichtbare punten. Zoom eerst verder in of laat de expliciete LOD-weergave actief.",
                        "Volledige resolutie geblokkeerd", MessageBoxButton.OK, MessageBoxImage.Information);
                    OnPropertyChanged(nameof(UseDownsampling));
                    return;
                }
            }

            if (SetField(ref _useDownsampling, value))
            {
                ApplyVisualOptionsPreservingView();
            }
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
                ApplyVisualOptionsPreservingView();
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

    public bool LinkYAxisAcrossPanels
    {
        get => _linkYAxisAcrossPanels;
        set
        {
            if (SetField(ref _linkYAxisAcrossPanels, value))
            {
                _xAxisSyncService.Configure(syncXAxis: true, syncYAxis: _linkYAxisAcrossPanels);
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _activeCursorPanel ??= Panels.FirstOrDefault();
        _xAxisSyncService.Configure(syncXAxis: true, syncYAxis: LinkYAxisAcrossPanels);
        _xAxisSyncService.Bind(Panels.Select(panel => panel.PlotModel));
        RefreshCursorAnnotations();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _xAxisSyncService.Bind([]);
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

        _xAxisSyncService.Configure(syncXAxis: true, syncYAxis: LinkYAxisAcrossPanels);
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
        CursorInfoText.Text = $"Cursor: {cursorText} | A: {aText} | B: {bText} | dt: {dtText}";
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
        if (_suppressVisualRefresh || Panels.Count == 0)
        {
            return;
        }

        var snapshots = CapturePanelSnapshots();
        foreach (var panel in Panels)
        {
            var model = panel.PlotModel;
            var xAxis = model.Axes.FirstOrDefault(static axis => axis.Position == AxisPosition.Bottom);
            var visibleMinimum = xAxis?.ActualMinimum;
            var visibleMaximum = xAxis?.ActualMaximum;
            model.IsLegendVisible = ShowLegend;
            model.Series.Clear();

            foreach (var series in panel.SeriesData)
            {
                var rendered = BuildSeries(series, visibleMinimum, visibleMaximum);
                model.Series.Add(rendered);
            }
        }

        RestorePanelSnapshots(snapshots);
        _xAxisSyncService.Configure(syncXAxis: true, syncYAxis: LinkYAxisAcrossPanels);
        _xAxisSyncService.Bind(Panels.Select(panel => panel.PlotModel));
        RefreshCursorAnnotations();
    }

    private OxyPlot.Series.Series BuildSeries(RenderedSeriesData series, double? visibleMinimum, double? visibleMaximum)
    {
        var start = FindFirstAtOrAfter(series.Time, visibleMinimum);
        var end = FindFirstAfter(series.Time, visibleMaximum);
        if (end <= start) { start = 0; end = series.Time.Length; }
        var visibleTime = start == 0 && end == series.Time.Length ? series.Time : series.Time[start..end];
        var visibleValue = start == 0 && end == series.Value.Length ? series.Value : series.Value[start..end];
        (double[] x, double[] y) = UseDownsampling
            ? Downsampling.MinMax(visibleTime, visibleValue, Math.Clamp(MaxPointsPerTrace, 200, 200_000))
            : (visibleTime, visibleValue);
        var title = x.Length < visibleTime.Length
            ? $"{series.Label} — weergegeven {x.Length:N0} / zichtbaar {visibleTime.Length:N0} / totaal {series.Time.Length:N0}"
            : series.Label;
        if (MarkersOnly)
        {
            var scatter = new ScatterSeries
            {
                Title = title,
                MarkerFill = series.Color,
                MarkerStroke = series.Color,
                MarkerType = MarkerType.Circle,
                MarkerSize = 2.5,
                YAxisKey = series.YAxisKey,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                TrackerFormatString = "{0}\nTijd: {2:G17}\nWaarde: {4:G17}"
            };

            for (var i = 0; i < x.Length; i++)
            {
                scatter.Points.Add(new ScatterPoint(x[i], y[i]));
            }

            return scatter;
        }

        if (StepPlot)
        {
            var stair = new StairStepSeries
            {
                Title = title,
                Color = series.Color,
                StrokeThickness = 1.2,
                YAxisKey = series.YAxisKey,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                Decimator = UseDownsampling ? Decimator.Decimate : null,
                TrackerFormatString = "{0}\nTijd: {2:G17}\nWaarde: {4:G17}"
            };

            for (var i = 0; i < x.Length; i++)
            {
                stair.Points.Add(new DataPoint(x[i], y[i]));
            }

            return stair;
        }

        var line = new LineSeries
        {
            Title = title,
            Color = series.Color,
            StrokeThickness = 1.2,
            YAxisKey = series.YAxisKey,
            EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            Decimator = UseDownsampling ? Decimator.Decimate : null,
            TrackerFormatString = "{0}\nTijd: {2:G17}\nWaarde: {4:G17}"
        };

        for (var i = 0; i < x.Length; i++)
        {
            line.Points.Add(new DataPoint(x[i], y[i]));
        }

        return line;
    }

    private static int CountVisible(double[] values, double? minimum, double? maximum) =>
        Math.Max(0, FindFirstAfter(values, maximum) - FindFirstAtOrAfter(values, minimum));

    private static int FindFirstAtOrAfter(double[] values, double? threshold)
    {
        if (!threshold.HasValue || double.IsNaN(threshold.Value) || double.IsInfinity(threshold.Value)) return 0;
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (values[middle] < threshold.Value) low = middle + 1; else high = middle;
        }

        return low;
    }

    private static int FindFirstAfter(double[] values, double? threshold)
    {
        if (!threshold.HasValue || double.IsNaN(threshold.Value) || double.IsInfinity(threshold.Value)) return values.Length;
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (values[middle] <= threshold.Value) low = middle + 1; else high = middle;
        }

        return low;
    }

    private List<PanelViewSnapshot> CapturePanelSnapshots()
    {
        var snapshots = new List<PanelViewSnapshot>(Panels.Count);
        foreach (var panel in Panels)
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

    private sealed record AxisRange(double Minimum, double Maximum);

    private sealed record PanelViewSnapshot(
        IReadOnlyDictionary<string, AxisRange> AxisRanges,
        IReadOnlyDictionary<string, bool> SeriesVisibility);
}
