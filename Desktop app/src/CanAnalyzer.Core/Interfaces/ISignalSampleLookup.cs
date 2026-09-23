using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>Signal summaries and plot values without rescanning all decoded samples.</summary>
public interface ISignalSampleLookup
{
    IReadOnlyList<SignalSampleSummary> GetSignalSummaries();
    IEnumerable<SignalSeriesPoint> ReadSignalSeries(SignalIdentity identity);
}

public sealed record SignalSampleSummary(int Count, double Minimum, double Maximum, DecodedSignalSample Latest);

public readonly record struct SignalSeriesPoint(long TimestampNanoseconds, long FrameIndex, double Value);
