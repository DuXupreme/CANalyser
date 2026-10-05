using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Services;

/// <summary>
/// Opens plot panels in a dedicated detached window.
/// </summary>
public interface IPlotWindowService
{
    void ShowPlots(
        IReadOnlyList<PlotPanelModel> panels,
        PlotViewOptions renderedOptions,
        DisplayLimits displayLimits,
        bool linkXAxisAcrossPanels,
        bool linkYAxisAcrossPanels,
        DateTimeOffset? startTimeUtc);
}
