using OxyPlot;

namespace CanAnalyzer.App.Models;

/// <summary>
/// Full rendered (filtered/transformed) series used for cursor/flag measurements.
/// </summary>
public sealed record RenderedSeriesData(
    string Label,
    double[] Time,
    double[] Value,
    string YAxisKey,
    OxyColor Color)
{
    public IReadOnlyList<CanAnalyzer.Core.Domain.MeasurementGap> Gaps { get; init; } = [];
    private CanAnalyzer.Core.Analysis.SignalRangeIndex? _rangeIndex;
    public CanAnalyzer.Core.Analysis.SignalRangeIndex RangeIndex
    {
        get => _rangeIndex ??= new(Time, Value);
        init => _rangeIndex = value;
    }
}
