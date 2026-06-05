using OxyPlot;

namespace CanAnalyzer.App.Models;

/// <summary>
/// One rendered panel in the Analysis view.
/// </summary>
public sealed class PlotPanelModel
{
    public required string Title { get; init; }

    public required PlotModel PlotModel { get; init; }

    public required IPlotController PlotController { get; init; }

    public required IReadOnlyList<RenderedSeriesData> SeriesData { get; init; }
}
