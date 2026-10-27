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
        get => GetRangeIndex();
        init => _rangeIndex = value;
    }

    public CanAnalyzer.Core.Analysis.SignalRangeIndex GetRangeIndex(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existing = Volatile.Read(ref _rangeIndex);
        if (existing is not null) return existing;
        var created = new CanAnalyzer.Core.Analysis.SignalRangeIndex(Time, Value, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Interlocked.CompareExchange(ref _rangeIndex, created, null) ?? created;
    }
}
