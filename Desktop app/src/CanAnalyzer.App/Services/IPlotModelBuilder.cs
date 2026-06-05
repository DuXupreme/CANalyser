using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Services;

/// <summary>
/// Builds OxyPlot models from dataset + group/view options.
/// </summary>
public interface IPlotModelBuilder
{
    IReadOnlyList<PlotPanelModel> Build(
        CanDataset dataset,
        IReadOnlyList<PlotGroup> plotGroups,
        PlotViewOptions viewOptions);
}
