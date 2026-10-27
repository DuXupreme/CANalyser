using OxyPlot;
using OxyPlot.Series;

namespace CanAnalyzer.App.Services;

/// <summary>Full-domain axis bounds are independent of the viewport point buffer.
/// This avoids copying overview/detail lists twice on every update.</summary>
internal interface IOverviewSeries
{
    void ResetWindow();
}

internal sealed class OverviewLineSeries(List<DataPoint> overview) : LineSeries, IOverviewSeries
{
    protected override void UpdateMaxMin()
    {
        MinX = MaxX = MinY = MaxY = double.NaN;
        InternalUpdateMaxMin(overview);
    }
    public void ResetWindow() => WindowStartIndex = 0;
}

internal sealed class OverviewStepSeries(List<DataPoint> overview) : StairStepSeries, IOverviewSeries
{
    protected override void UpdateMaxMin()
    {
        MinX = MaxX = MinY = MaxY = double.NaN;
        InternalUpdateMaxMin(overview);
    }
    public void ResetWindow() => WindowStartIndex = 0;
}

internal sealed class OverviewScatterSeries(List<ScatterPoint> overview) : ScatterSeries, IOverviewSeries
{
    protected override void UpdateMaxMin()
    {
        // Preserve ScatterSeries' size/color bookkeeping, then restore full X/Y bounds.
        base.UpdateMaxMin();
        MinX = MaxX = MinY = MaxY = double.NaN;
        InternalUpdateMaxMin(overview, static p => p.X, static p => p.Y);
    }
    public void ResetWindow() => WindowStartIndex = 0;
}
