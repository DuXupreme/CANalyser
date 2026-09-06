using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;

namespace CanAnalyzer.App.Infrastructure;

/// <inheritdoc />
public sealed class PlotWindowService : IPlotWindowService
{
    public void ShowPlots(
        IReadOnlyList<PlotPanelModel> panels,
        int subplotHeight,
        int maxPointsPerTrace,
        bool useDownsampling,
        bool linkXAxisAcrossPanels,
        bool linkYAxisAcrossPanels,
        DateTimeOffset? startTimeUtc)
    {
        if (panels.Count == 0)
        {
            return;
        }

        var window = new PlotPanelsWindow(
            panels,
            subplotHeight,
            maxPointsPerTrace,
            useDownsampling,
            linkXAxisAcrossPanels,
            linkYAxisAcrossPanels,
            startTimeUtc);
        window.Show();
        window.Activate();
    }
}
