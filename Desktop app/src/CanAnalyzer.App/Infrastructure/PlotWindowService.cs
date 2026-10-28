using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Infrastructure;

/// <inheritdoc />
public sealed class PlotWindowService : IPlotWindowService
{
    public void ShowPlots(
        IReadOnlyList<PlotPanelModel> panels,
        PlotViewOptions renderedOptions,
        DisplayLimits displayLimits,
        bool linkXAxisAcrossPanels,
        bool linkYAxisAcrossPanels,
        DateTimeOffset? startTimeUtc,
        Action<double>? cursorChanged = null)
    {
        if (panels.Count == 0)
        {
            return;
        }

        var window = new PlotPanelsWindow(
            panels,
            renderedOptions,
            displayLimits,
            linkXAxisAcrossPanels,
            linkYAxisAcrossPanels,
            startTimeUtc,
            cursorChanged);
        window.Show();
        window.Activate();
    }
}
