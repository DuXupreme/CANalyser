using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.CompilerServices;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace CanAnalyzer.App.Views;

/// <summary>Time plots share navigation, flags and local viewport detail. Source points
/// remain immutable and separate from display sampling.</summary>
public sealed class TimePlotView : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(PlotModel),
        typeof(TimePlotView), new PropertyMetadata(null, (d, e) => ((TimePlotView)d).ChangeModel((PlotModel?)e.NewValue)));
    public PlotModel? Model { get => (PlotModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public static readonly DependencyProperty InteractionGroupProperty = DependencyProperty.Register(nameof(InteractionGroup), typeof(string),
        typeof(TimePlotView), new PropertyMetadata("default", (d, _) => ((TimePlotView)d).ChangeScope()));
    public string InteractionGroup { get => (string)GetValue(InteractionGroupProperty); set => SetValue(InteractionGroupProperty, value); }
    public static readonly RoutedEvent CursorMovedEvent = EventManager.RegisterRoutedEvent(nameof(CursorMoved),
        RoutingStrategy.Bubble, typeof(EventHandler<TimePlotCursorEventArgs>), typeof(TimePlotView));
    public event EventHandler<TimePlotCursorEventArgs> CursorMoved { add => AddHandler(CursorMovedEvent, value); remove => RemoveHandler(CursorMovedEvent, value); }
    private readonly CachedPlotView _plot = new() { Controller = TimePlotController.Create() };
    private readonly PlotCursorOverlay _overlay = new();
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _cursorStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _valueStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _exact = new() { Content = "Alle punten in dit tijdvak", Margin = new Thickness(8, 0, 4, 0) };
    private readonly PlotInteractionHistory _history;
    private static readonly ConditionalWeakTable<object, Dictionary<string, InteractionScope>> Scopes = new();
    private InteractionScope _scope = new();
    private IReadOnlyList<RenderedSeriesData> _sources = [];
    private PlotModel? _boundModel;
    private Axis? _axis;
    private CancellationTokenSource? _build;
    private double? _cursor, _a, _b;
    private bool _updatingToggle, _unloaded, _hasReducedSource;
    private DateTime _lastCursor;
    private const int PointBudget = 4000;

    public TimePlotView()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new WrapPanel();
        AddButton(toolbar, "A", () => _scope.SetState(_cursor, _cursor, _b), "Ctrl+klik: vlag A");
        AddButton(toolbar, "B", () => _scope.SetState(_cursor, _a, _cursor), "Shift+klik: vlag B");
        AddButton(toolbar, "Wis A/B", () => _scope.SetState(_cursor, null, null));
        AddButton(toolbar, "Terug", () => _history!.Undo(), "Ctrl+Z: vorige uitsnede");
        AddButton(toolbar, "Reset", Reset, "Dubbelklik: volledig tijdvak");
        toolbar.Children.Add(_exact);
        ToolTipService.SetShowOnDisabled(_exact, true);
        grid.Children.Add(toolbar);
        var chart = new Grid(); chart.Children.Add(_plot); chart.Children.Add(_overlay);
        Grid.SetRow(chart, 1); grid.Children.Add(chart);
        var footer = new StackPanel(); footer.Children.Add(_cursorStatus); footer.Children.Add(_valueStatus); footer.Children.Add(_status);
        Grid.SetRow(footer, 2); grid.Children.Add(footer); Content = grid;
        _history = new PlotInteractionHistory(this, () => _scope.Models);
        _history.UseState(_scope.History); _scope.Add(this);
        DataContextChanged += (_, _) => ChangeScope();
        _exact.Checked += (_, _) => { if (!_updatingToggle) _ = RebuildAsync(true); };
        _exact.Unchecked += (_, _) => { if (!_updatingToggle) ResumeOverview(); };
        _plot.MouseMove += OnMouseMove; _plot.PreviewMouseDown += OnMouseDown;
        Loaded += (_, _) =>
        {
            _unloaded = false;
            _scope.Add(this);
            if (_axis is not null) { _axis.AxisChanged -= OnAxisChanged; _axis.AxisChanged += OnAxisChanged; }
            if (Model is not null && _plot.Model is null) _ = RebuildAsync(false);
            else if (_boundModel is { } model) ViewportSampling.Resume(model);
        };
        Unloaded += (_, _) => { _unloaded = true; _scope.Remove(this); _build?.Cancel(); if (_axis is not null) _axis.AxisChanged -= OnAxisChanged; if (_boundModel is { } model) ViewportSampling.Suspend(model, _plot); };
        DrawCursor();
    }
    private static void AddButton(Panel panel, string label, Action action, string? tip = null)
    {
        var button = new Button { Content = label, ToolTip = tip, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 3, 0) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void ChangeScope()
    {
        _scope.Remove(this);
        var groups = Scopes.GetValue(DataContext ?? this, _ => new(StringComparer.Ordinal));
        var group = InteractionGroup ?? "default";
        if (!groups.TryGetValue(group, out var scope)) groups.Add(group, scope = new());
        _scope = scope; _history.UseState(_scope.History);
        if (!_unloaded) _scope.Add(this);
    }
    private void ChangeModel(PlotModel? model)
    {
        _build?.Cancel();
        if (_axis is not null) _axis.AxisChanged -= OnAxisChanged;
        if (_boundModel is { } old) ViewportSampling.Suspend(old, _plot);
        _boundModel = model; _plot.Model = null; _overlay.Model = model; _axis = null; _sources = [];
        _hasReducedSource = model is not null && PlotSourceMetadata.IsReducedPreview(model);
        _exact.IsEnabled = !_hasReducedSource;
        _exact.ToolTip = _hasReducedSource
            ? "Deze bronpreview is vooraf gereduceerd. De volledige meetreeks is niet beschikbaar in deze grafiek; 'Alle punten' kan die niet herstellen."
            : null;
        _history.Clear(); _scope.ModelChanged(this); SetExact(false); DrawCursor();
        if (model is null) { _status.Text = "Geen grafiekbron."; return; }
        _axis = model.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);
        _sources = CaptureSources(model);
        _scope.ModelReady(this);
        _ = RebuildAsync(false);
    }
    internal static IReadOnlyList<RenderedSeriesData> CaptureSources(PlotModel model)
    {
        var result = new List<RenderedSeriesData>();
        foreach (var line in model.Series.OfType<LineSeries>())
        {
            var times = new List<double>(); var values = new List<double>(); var gaps = new List<MeasurementGap>();
            var broken = false;
            foreach (var point in line.Points)
            {
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) { broken = true; continue; }
                if (broken && times.Count > 0) gaps.Add(new(times[^1], point.X));
                broken = false; times.Add(point.X); values.Add(point.Y);
            }
            result.Add(new(line.Title ?? "Signaal", times.ToArray(), values.ToArray(), line.YAxisKey!, line.Color) { Gaps = gaps });
        }
        return result;
    }
    private async Task RebuildAsync(bool exact)
    {
        _build?.Cancel();
        var cancellation = new CancellationTokenSource(); _build = cancellation;
        var model = _boundModel; var axis = _axis; var sources = _sources;
        if (model is null) { _build = null; cancellation.Dispose(); return; }
        if (_hasReducedSource) exact = false;
        var viewport = axis is not null && double.IsFinite(axis.ActualMinimum) && double.IsFinite(axis.ActualMaximum) && axis.ActualMaximum > axis.ActualMinimum
            ? new PlotViewport(axis.ActualMinimum, axis.ActualMaximum) : (PlotViewport?)null;
        if (exact && viewport is null) { SetExact(false); exact = false; }
        var styles = model.Series.OfType<LineSeries>().Select(line => (Step: line is StairStepSeries, line.LineStyle, line.StrokeThickness, line.IsVisible)).ToArray();
        _status.Text = exact ? "Exacte bronpunten voor zichtbaar tijdvak voorbereiden…" : "Grafiek voorbereiden…";
        try
        {
            var prepared = await Task.Run(() => sources.Select((source, index) =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var series = (LineSeries)PlotSeriesRenderer.Build(source, new PlotViewOptions
                { UseDownsampling = !exact, MaxPointsPerTrace = PointBudget, StepPlot = index < styles.Length && styles[index].Step }, viewport, cancellation.Token);
                if (index < styles.Length) { series.LineStyle = styles[index].LineStyle; series.StrokeThickness = styles[index].StrokeThickness; series.IsVisible = styles[index].IsVisible; }
                return series;
            }).ToArray(), cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(model, _boundModel) || _unloaded) return;
            var nonLines = model.Series.Where(series => series is not LineSeries).ToArray();
            model.Series.Clear(); foreach (var line in prepared) model.Series.Add(line); foreach (var other in nonLines) model.Series.Add(other);
            ViewportSampling.Attach(model, sources, !exact, PointBudget, viewport);
            _plot.Model = model;
            if (axis is not null) { axis.AxisChanged -= OnAxisChanged; axis.AxisChanged += OnAxisChanged; }
            SetExact(exact);
            _status.Text = exact ? "Exact: alle bronpunten van deze grafiek in het zichtbare tijdvak. Navigeren herstelt overzicht." : OverviewStatus;
            model.InvalidatePlot(true); DrawCursor();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (ReferenceEquals(model, _boundModel)) { _status.Text = $"Grafiek voorbereiden mislukt: {ex.Message}"; _plot.Model = model; } }
        finally { if (ReferenceEquals(_build, cancellation)) _build = null; cancellation.Dispose(); }
    }
    private void OnAxisChanged(object? sender, AxisChangedEventArgs e)
    {
        // A sibling may establish the shared initial range while this first
        // overview is still being prepared. Keep that initial build alive.
        if (_plot.Model is not null) ResumeOverview();
        if (sender is Axis axis) _scope.SyncRange(axis);
    }
    private void ResumeOverview()
    {
        _build?.Cancel(); SetExact(false);
        if (_boundModel is { } model) ViewportSampling.ResumeDownsampling(model, PointBudget);
        _status.Text = OverviewStatus;
    }
    private string OverviewStatus => _hasReducedSource
        ? "Bronpreview: vooraf beperkt; geen volledige meetreeks. 'Alle punten' is hier niet beschikbaar."
        : "Overzicht: weergave aangepast aan zichtbaar tijdvak; bronpunten blijven behouden.";
    private void SetExact(bool value) { _updatingToggle = true; _exact.IsChecked = value; _updatingToggle = false; }
    private void Reset()
    {
        if (_boundModel is null) return;
        _history.Capture(); _scope.Reset();
    }
    private bool MouseTime(MouseEventArgs e, out double time)
    {
        var point = e.GetPosition(_plot); time = double.NaN;
        if (_axis is null || _boundModel is null || !_boundModel.PlotArea.Contains(new ScreenPoint(point.X, point.Y))) return false;
        time = _axis.InverseTransform(point.X); return double.IsFinite(time);
    }
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed ||
            (DateTime.UtcNow - _lastCursor).TotalMilliseconds < 60 || !MouseTime(e, out var time)) return;
        _lastCursor = DateTime.UtcNow; SetCursor(time);
    }
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (TryToggleLegend(e.GetPosition(_plot))) { e.Handled = true; return; }
        if (e.ClickCount >= 2) { Reset(); e.Handled = true; return; }
        if (!MouseTime(e, out var time)) return;
        SetCursor(time);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { _scope.SetState(_cursor, _cursor, _b); e.Handled = true; }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { _scope.SetState(_cursor, _a, _cursor); e.Handled = true; }
    }
    internal void SetCursor(double time)
    {
        if (!double.IsFinite(time)) return;
        _scope.SetState(time, _a, _b); RaiseEvent(new TimePlotCursorEventArgs(CursorMovedEvent, time));
    }
    private bool TryToggleLegend(Point point)
    {
        if (_boundModel is not { } model) return false;
        var position = new ScreenPoint(point.X, point.Y);
        if (!model.Legends.Any(legend => legend.LegendArea.Contains(position))) return false;
        var series = model.HitTest(new HitTestArguments(position, 6))
            .SelectMany(hit => new Series?[] { hit.Element as Series, hit.Item as Series }).FirstOrDefault(item => item is not null);
        if (series is null) return false;
        series.IsVisible = !series.IsVisible; model.InvalidatePlot(false); return true;
    }
    private void DrawCursor()
    {
        _cursorStatus.Text = $"Cursor: {Format(_cursor)} · A: {Format(_a)} · B: {Format(_b)} · Δt: {Format(_a.HasValue && _b.HasValue ? _b - _a : null)}";
        var values = _cursor is { } time ? _sources.Select(source => CursorValue(source, time)).ToArray() : [];
        _valueStatus.Text = string.Join(" · ", values.Take(3));
        _valueStatus.ToolTip = string.Join(Environment.NewLine, values);
        if (_boundModel is not { } model) return;
        var annotations = new List<Annotation>();
        void Add(double? value, string name, OxyColor color)
        {
            if (value is { } x) annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = x, Text = name, Color = color, LineStyle = LineStyle.Dash });
        }
        Add(_cursor, "Cursor", OxyColors.Gray); Add(_a, "A", OxyColors.DodgerBlue); Add(_b, "B", OxyColors.OrangeRed);
        PlotCursorAnnotations.Set(model, annotations);
    }
    internal static string CursorValue(RenderedSeriesData source, double time)
    {
        // Never display a future value or carry a value through a declared gap as current.
        var left = 0; var right = source.Time.Length;
        while (left < right) { var middle = left + (right - left) / 2; if (source.Time[middle] <= time) left = middle + 1; else right = middle; }
        if (left == 0) return $"{source.Label}: geen sample vóór cursor";
        var index = left - 1;
        var gap = source.Gaps.Any(g => g.StartSeconds >= source.Time[index] && g.StartSeconds < time && g.EndSeconds > source.Time[index]);
        return $"{source.Label}: {source.Value[index]:G9} (t={source.Time[index]:G9} s{(gap ? "; meetgat" : "")})";
    }
    /// <summary>One analysis viewmodel owns one interaction scope; no application-wide axis state.</summary>
    private sealed class InteractionScope
    {
        private readonly List<WeakReference<TimePlotView>> _views = [];
        private bool _syncing, _navigated;
        private double? _cursor, _a, _b;
        private (double Minimum, double Maximum)? _range;
        private WeakReference<TimePlotView>? _inspectionSource;
        public PlotInteractionHistory.State History { get; } = new();
        private IReadOnlyList<TimePlotView> Views
        {
            get
            {
                _views.RemoveAll(reference => !reference.TryGetTarget(out _));
                return _views.Select(reference => reference.TryGetTarget(out var view) ? view : null).OfType<TimePlotView>().ToArray();
            }
        }
        public IEnumerable<PlotModel> Models => Views.Select(view => view._boundModel).OfType<PlotModel>();
        public void Add(TimePlotView view)
        {
            if (!Views.Contains(view)) _views.Add(new(view));
            ApplyState(view);
            ApplyRange(view);
        }
        public void Remove(TimePlotView view)
        {
            _views.RemoveAll(reference => !reference.TryGetTarget(out var existing) || ReferenceEquals(existing, view));
            // Undo snapshots hold models/axes. Retire them with an unloaded view so
            // a long-lived viewmodel cannot keep its old visual tree alive.
            History.Entries.Clear();
        }
        public void ModelChanged(TimePlotView view)
        {
            // A new event/preview model defines a new inspection interval. Retaining
            // the previous event's manual zoom could leave the new event off-screen.
            _navigated = false;
            _range = null;
            if (_inspectionSource?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, view))
                _inspectionSource = null;
            if (!Views.Any(member => member._sources.Any(source => source.Time.Length > 0)))
            {
                _cursor = _a = _b = null; _range = null; _navigated = false;
                foreach (var member in Views) ApplyState(member);
            }
            ApplyState(view);
        }
        public void SetState(double? cursor, double? a, double? b)
        {
            _cursor = cursor; _a = a; _b = b;
            foreach (var view in Views) ApplyState(view);
        }
        private void ApplyState(TimePlotView view)
        {
            view._cursor = _cursor; view._a = _a; view._b = _b; view.DrawCursor();
        }
        public void SyncRange(Axis axis)
        {
            if (_syncing || !double.IsFinite(axis.ActualMinimum) || !double.IsFinite(axis.ActualMaximum) || axis.ActualMaximum <= axis.ActualMinimum) return;
            _navigated = true;
            _range = (axis.ActualMinimum, axis.ActualMaximum);
            try
            {
                _syncing = true;
                foreach (var view in Views) if (!ReferenceEquals(view._axis, axis)) ApplyRange(view);
            }
            finally { _syncing = false; }
        }
        private void ApplyRange(TimePlotView view)
        {
            if (_range is not { } range || view._axis is not { } axis || view._boundModel is not { } model) return;
            if (axis.ActualMinimum == range.Minimum && axis.ActualMaximum == range.Maximum) return;
            axis.Zoom(range.Minimum, range.Maximum); model.InvalidatePlot(false);
        }
        public void Reset()
        {
            _navigated = false;
            var views = Views;
            _range = InitialRange();
            try
            {
                _syncing = true;
                foreach (var view in views)
                {
                    view.ResumeOverview(); view._boundModel?.ResetAllAxes();
                    ApplyRange(view); view._boundModel?.InvalidatePlot(false);
                }
            }
            finally { _syncing = false; }
        }
        public void ModelReady(TimePlotView view)
        {
            // Explicit limits describe the newly selected event/preview, including
            // its padding or an interval with no samples. Old sibling points must
            // not expand that inspection back to a whole-log overview.
            if (ExplicitRange(view) is not null) _inspectionSource = new(view);
            if (_navigated) { ApplyRange(view); return; }
            _range = InitialRange();
            try { _syncing = true; foreach (var member in Views) ApplyRange(member); }
            finally { _syncing = false; }
        }
        private (double Minimum, double Maximum)? InitialRange()
        {
            var views = Views;
            if (_inspectionSource?.TryGetTarget(out var owner) == true && views.Contains(owner)
                && ExplicitRange(owner) is { } inspection) return inspection;
            _inspectionSource = null;
            var ranges = views.SelectMany(member => member._sources).Where(source => source.Time.Length > 0)
                .Select(source => (First: source.Time[0], Last: source.Time[^1])).ToArray();
            return ranges.Length > 0 && ranges.Max(range => range.Last) > ranges.Min(range => range.First)
                ? (ranges.Min(range => range.First), ranges.Max(range => range.Last)) : null;
        }
        private static (double Minimum, double Maximum)? ExplicitRange(TimePlotView view)
            => view._axis is { } axis && double.IsFinite(axis.Minimum) && double.IsFinite(axis.Maximum) && axis.Maximum > axis.Minimum
                ? (axis.Minimum, axis.Maximum) : null;
    }

    private static string Format(double? value) => value is { } time ? $"{time:G9} s" : "—";
}

public sealed class TimePlotCursorEventArgs(RoutedEvent routedEvent, double timeSeconds) : RoutedEventArgs(routedEvent)
{
    public double TimeSeconds { get; } = timeSeconds;
    protected override void InvokeEventHandler(Delegate genericHandler, object genericTarget) =>
        ((EventHandler<TimePlotCursorEventArgs>)genericHandler)(genericTarget, this);
}
