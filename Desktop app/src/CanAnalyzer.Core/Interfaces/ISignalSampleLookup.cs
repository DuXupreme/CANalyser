using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>Signal summaries and plot values without rescanning all decoded samples.</summary>
public interface ISignalSampleLookup
{
    IReadOnlyList<SignalSampleSummary> GetSignalSummaries();
    IEnumerable<SignalSeriesPoint> ReadSignalSeries(SignalIdentity identity);

    /// <summary>
    /// Reads at most <paramref name="maximumPoints"/> points spread over the complete signal.
    /// Implementations backed by an index can override this to avoid materializing or scanning
    /// the complete series.
    /// </summary>
    IEnumerable<SignalSeriesPoint> ReadSignalSeriesSampled(SignalIdentity identity, int maximumPoints)
    {
        if (maximumPoints < 2) throw new ArgumentOutOfRangeException(nameof(maximumPoints));
        var count = GetSignalSummaries().FirstOrDefault(summary => summary.Latest.Identity == identity)?.Count ?? 0;
        if (count <= maximumPoints)
        {
            foreach (var point in ReadSignalSeries(identity)) yield return point;
            yield break;
        }

        var target = 0L;
        var outputIndex = 0;
        var sourceIndex = 0L;
        foreach (var point in ReadSignalSeries(identity))
        {
            if (sourceIndex == target)
            {
                yield return point;
                outputIndex++;
                if (outputIndex >= maximumPoints) yield break;
                target = (long)outputIndex * (count - 1L) / (maximumPoints - 1L);
            }

            sourceIndex++;
        }
    }
}

public sealed record SignalSampleSummary(int Count, double Minimum, double Maximum, DecodedSignalSample Latest);

public readonly record struct SignalSeriesPoint(long TimestampNanoseconds, long FrameIndex, double Value);
