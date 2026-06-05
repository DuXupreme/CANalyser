using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.State;

/// <summary>
/// Persisted desktop application settings.
/// </summary>
public sealed class AppSettings
{
    public string? LastLogFilePath { get; set; }

    public string? LastDbcFilePath { get; set; }

    public List<string> RecentLogFiles { get; set; } = [];

    public List<string> RecentDbcFiles { get; set; } = [];

    public double WindowWidth { get; set; } = 1600;

    public double WindowHeight { get; set; } = 960;

    public double WindowLeft { get; set; } = 120;

    public double WindowTop { get; set; } = 60;

    public bool WindowMaximized { get; set; }

    public PlotViewOptions LastPlotViewOptions { get; set; } = new();

    public RawFrameFilterOptions LastRawFrameFilter { get; set; } = new();
}
