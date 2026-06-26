using CanAnalyzer.App.Models;

namespace CanAnalyzer.App.Services;

/// <summary>
/// Opens plot panels in a dedicated detached window.
/// </summary>
public interface IPlotWindowService
{
    void ShowPlots(
        IReadOnlyList<PlotPanelModel> panels,
        int subplotHeight,
        int maxPointsPerTrace,
        bool useDownsampling,
        bool linkXAxisAcrossPanels,
        bool linkYAxisAcrossPanels);
}
