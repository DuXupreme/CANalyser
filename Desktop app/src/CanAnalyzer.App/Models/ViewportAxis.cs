using OxyPlot.Axes;

namespace CanAnalyzer.App.Models;

/// <summary>Exposes the current user range so async publication can preserve a pending reset.</summary>
public sealed class ViewportAxis : LinearAxis
{
    public bool HasUserRange => double.IsFinite(ViewMinimum) && double.IsFinite(ViewMaximum) && ViewMaximum > ViewMinimum;
    public double VisibleMinimum => HasUserRange ? ViewMinimum : ActualMinimum;
    public double VisibleMaximum => HasUserRange ? ViewMaximum : ActualMaximum;

    protected override void OnAxisChanged(AxisChangedEventArgs args)
    {
        // OxyPlot normally updates these transforms during the immediate redraw.
        // With queued redraws, the next mouse event must already see the new range.
        RefreshNavigationTransform();
        base.OnAxisChanged(args);
    }

    private void RefreshNavigationTransform()
    {
        if (!double.IsFinite(ActualMinimum) || !double.IsFinite(ActualMaximum) ||
            ActualMaximum <= ActualMinimum || (!IsHorizontal() && !IsVertical())) return;

        // ScreenMin/Max already include axis direction, Start/EndPosition and
        // Minimum/MaximumMargin from the last layout. Data margins are additional.
        var startPixel = IsHorizontal() ? ScreenMin.X : ScreenMax.Y;
        var endPixel = IsHorizontal() ? ScreenMax.X : ScreenMin.Y;
        if (!double.IsFinite(startPixel) || !double.IsFinite(endPixel) || startPixel == endPixel) return;
        var direction = (IsHorizontal() ^ IsReversed) ? 1d : -1d;
        var dataStartPixel = startPixel + Math.Max(0, MinimumDataMargin) * direction;
        var dataEndPixel = endPixel - Math.Max(0, MaximumDataMargin) * direction;
        var scale = (dataEndPixel - dataStartPixel) / (ActualMaximum - ActualMinimum);
        if (!double.IsFinite(scale) || scale == 0) return;
        var offset = ActualMinimum - dataStartPixel / scale;
        if (!double.IsFinite(offset)) return;

        SetTransform(scale, offset);
        ClipMinimum = MinimumDataMargin > 0 ? InverseTransform(startPixel) : ActualMinimum;
        ClipMaximum = MaximumDataMargin > 0 ? InverseTransform(endPixel) : ActualMaximum;
    }
}
