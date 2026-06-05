using OxyPlot;

namespace CanAnalyzer.App.Services;

/// <summary>
/// Keeps X-axis view ranges synchronized across multiple plot panels.
/// </summary>
public interface IXAxisSyncService
{
    void Configure(bool syncXAxis, bool syncYAxis);

    void Bind(IEnumerable<PlotModel> models);
}
