using OxyPlot.Axes;

namespace CanAnalyzer.App.Models;

/// <summary>Exposes the current user range so async publication can preserve a pending reset.</summary>
public sealed class ViewportAxis : LinearAxis
{
    public bool HasUserRange => double.IsFinite(ViewMinimum) && double.IsFinite(ViewMaximum) && ViewMaximum > ViewMinimum;
    public double VisibleMinimum => HasUserRange ? ViewMinimum : ActualMinimum;
    public double VisibleMaximum => HasUserRange ? ViewMaximum : ActualMaximum;
}
