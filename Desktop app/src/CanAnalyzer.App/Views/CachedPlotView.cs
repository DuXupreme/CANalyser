using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OxyPlot;
using OxyPlot.Wpf;

namespace CanAnalyzer.App.Views;

/// <summary>Reuses unchanged, exactly measured WPF labels between redraws.</summary>
public sealed class CachedPlotView : PlotView
{
    protected override IRenderContext CreateRenderContext() => new CachedCanvasRenderContext(Canvas);
}

/// <summary>
/// Bounded text caches around the standard renderer. Cache misses use OxyPlot's original WPF
/// text path, including Unicode shaping, rotation and clipping. Geometry remains unchanged.
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
