using OxyPlot;

namespace CanAnalyzer.App.Models;

/// <summary>
/// Full rendered (filtered/transformed) series used for cursor/flag measurements.
/// </summary>
public sealed record RenderedSeriesData(
    string Label,
    float[] Time,
    float[] Value,
    string YAxisKey,
    OxyColor Color);
