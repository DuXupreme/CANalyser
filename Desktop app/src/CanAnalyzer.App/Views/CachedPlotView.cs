using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OxyPlot;
using OxyPlot.Wpf;
using CanAnalyzer.App.Services;

namespace CanAnalyzer.App.Views;

/// <summary>Coalesces navigation redraws and reuses exactly measured WPF labels.</summary>
public sealed class CachedPlotView : PlotView, IPlotView
{
    private bool _dirty;
    private bool _updateData;
    private int _workerRequestPosted;
    private int _workerUpdateData;
    private ScrollViewer? _scrollViewer;
    private ScrollContentPresenter? _viewport;
    private Window? _window;

    public CachedPlotView()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => ScheduleIfVisible();
        SizeChanged += (_, _) => ScheduleIfVisible();
    }

    // OxyPlot 2.1's non-virtual method updates AND renders synchronously on the UI
    // thread. Reimplement the interface used by PlotModel and mouse manipulators,
    // so X/Y synchronization cannot render the same panel twice in one input event.
    public new void InvalidatePlot(bool updateData = true)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (updateData) Interlocked.Exchange(ref _workerUpdateData, 1);
            if (Interlocked.Exchange(ref _workerRequestPosted, 1) == 0 && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    Interlocked.Exchange(ref _workerRequestPosted, 0);
                    InvalidatePlot(Interlocked.Exchange(ref _workerUpdateData, 0) != 0);
                }));
            return;
        }
        _dirty = true;
        _updateData |= updateData;
        ScheduleIfVisible();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ActualModel is { } model) ViewportSampling.Resume(model);
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollContentPresenter viewport && _viewport is null) _viewport = viewport;
            if (parent is ScrollViewer scrollViewer) { _scrollViewer = scrollViewer; break; }
        }
        if (_scrollViewer is not null) _scrollViewer.ScrollChanged += OnScrollChanged;
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += OnWindowStateChanged;
        ScheduleIfVisible();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ActualModel is { } model) ViewportSampling.Suspend(model, this);
        if (_scrollViewer is not null) _scrollViewer.ScrollChanged -= OnScrollChanged;
        if (_window is not null) _window.StateChanged -= OnWindowStateChanged;
        _scrollViewer = null; _viewport = null; _window = null;
        // Keep the dirty state: a tab/control can be loaded again at a newer range.
        PlotRenderScheduler.For(Dispatcher).Remove(this);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != ModelProperty) return;
        if (e.OldValue is PlotModel oldModel) ViewportSampling.Suspend(oldModel, this);
        if (e.NewValue is PlotModel newModel)
        {
            if (IsLoaded) ViewportSampling.Resume(newModel);
            else ViewportSampling.Suspend(newModel, this);
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => ScheduleIfVisible();
    private void OnWindowStateChanged(object? sender, EventArgs e) => ScheduleIfVisible();

    private bool CanRender()
    {
        if (!IsLoaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0 ||
            _window?.WindowState == WindowState.Minimized) return false;
        // Reparenting can make the view visible before WPF delivers Unloaded/Loaded.
        // Keep the dirty request until those events attach the new viewport.
        if (_viewport is not null && !_viewport.IsAncestorOf(this)) return false;
        return _viewport is null || TransformToAncestor(_viewport).TransformBounds(new Rect(RenderSize))
            .IntersectsWith(new Rect(_viewport.RenderSize));
    }

    private void ScheduleIfVisible()
    {
        if (_dirty && CanRender()) PlotRenderScheduler.For(Dispatcher).Request(this);
    }

    internal void FlushScheduledRender()
    {
        if (!_dirty || !CanRender()) return;
        var updateData = _updateData;
        _dirty = false; _updateData = false;
        using var activity = CanAnalyzer.App.Infrastructure.UiActivityDiagnostics.Begin(
            CanAnalyzer.App.Infrastructure.UiActivity.PlotRender);
        base.InvalidatePlot(updateData);
        PlotCursorAnnotations.PlotRendered(ActualModel);
    }

    protected override IRenderContext CreateRenderContext() => new CachedCanvasRenderContext(Canvas);
}

/// <summary>
/// Bounded text caches around the standard renderer. Cache misses use OxyPlot's original WPF
/// text path, including Unicode shaping, rotation and clipping. All marker positions are retained.
/// </summary>
public sealed class CachedCanvasRenderContext : CanvasRenderContext
{
    private readonly Canvas _canvas;
    public CachedCanvasRenderContext(Canvas canvas) : base(canvas) => _canvas = canvas;
    private const int Capacity = 1024;
    private readonly Dictionary<MeasureKey, OxySize> _sizes = [];
    private readonly Dictionary<TextKey, FrameworkElement> _labels = [];
    private OxyRect? _clip;
    private string? _tooltip;
    private readonly record struct MeasureKey(string Text, string Font, double Size, double Weight,
        TextMeasurementMethod Method, TextFormattingMode Formatting, double Dpi);
    private readonly record struct TextKey(MeasureKey Measure, ScreenPoint Position, OxyColor Color,
        double Rotation, OxyPlot.HorizontalAlignment Horizontal, OxyPlot.VerticalAlignment Vertical,
        OxySize? Maximum, OxyRect? Clip, Point Offset, string? Tooltip);

    public override void DrawEllipses(IList<OxyRect> rectangles, OxyColor fill, OxyColor stroke,
        double thickness, EdgeRenderingMode edgeRenderingMode)
    {
        // Dense overlapping stroked ellipses in one path can monopolize WPF's render
        // thread, including during Window.Close. Sixteen bounds that geometry work.
        // Keep alpha, different stroke colors and antialiasing on the original path:
        // their compositing depends on grouping. Opaque aliased batches preserve all
        // ellipses, with only subpixel contour rounding differences when rasterized.
        const int batchSize = 16;
        if (rectangles.Count <= batchSize || fill.A != 255 ||
            (stroke != fill && !stroke.IsUndefined() && thickness > 0) ||
            edgeRenderingMode != EdgeRenderingMode.PreferSpeed)
        {
            base.DrawEllipses(rectangles, fill, stroke, thickness, edgeRenderingMode);
            return;
        }
        var batch = new List<OxyRect>(batchSize);
        for (var start = 0; start < rectangles.Count; start += batchSize)
        {
            batch.Clear();
            for (var i = start; i < Math.Min(start + batchSize, rectangles.Count); i++) batch.Add(rectangles[i]);
            base.DrawEllipses(batch, fill, stroke, thickness, edgeRenderingMode);
        }
    }

    public override OxySize MeasureText(string text, string fontFamily, double fontSize, double fontWeight)
    {
        var key = new MeasureKey(text, fontFamily, fontSize, fontWeight, TextMeasurementMethod, TextFormattingMode, DpiScale);
        if (_sizes.TryGetValue(key, out var size)) return size;
        size = base.MeasureText(text, fontFamily, fontSize, fontWeight);
        if (_sizes.Count >= Capacity) _sizes.Clear();
        _sizes.Add(key, size);
        return size;
    }

    public override void DrawText(ScreenPoint p, string text, OxyColor fill, string fontFamily, double fontSize,
        double fontWeight, double rotate, OxyPlot.HorizontalAlignment halign, OxyPlot.VerticalAlignment valign,
        OxySize? maxSize)
    {
        var key = new TextKey(new(text, fontFamily, fontSize, fontWeight, TextMeasurementMethod, TextFormattingMode, DpiScale),
            p, fill, rotate, halign, valign, maxSize, _clip, VisualOffset, _tooltip);
        if (_labels.TryGetValue(key, out var label) && VisualTreeHelper.GetParent(label) is null)
        {
            _canvas.Children.Add(label);
            return;
        }
        var count = _canvas.Children.Count;
        base.DrawText(p, text, fill, fontFamily, fontSize, fontWeight, rotate, halign, valign, maxSize);
        if (_canvas.Children.Count == count + 1 && _canvas.Children[count] is FrameworkElement rendered)
        {
            if (_labels.Count >= Capacity) _labels.Clear();
            _labels[key] = rendered;
        }
    }

    protected override void SetClip(OxyRect clippingRect) { _clip = clippingRect; base.SetClip(clippingRect); }
    protected override void ResetClip() { _clip = null; base.ResetClip(); }
    public override void SetToolTip(string text) { _tooltip = text; base.SetToolTip(text); }
}
