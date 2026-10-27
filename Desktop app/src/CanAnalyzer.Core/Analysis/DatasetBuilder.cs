using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Analysis;

/// <inheritdoc />
public sealed class DatasetBuilder : IDatasetBuilder
{
    public CanDataset Build(
        IReadOnlyList<RawCanFrame> rawFrames,
        IReadOnlyList<DecodedSignalSample> decodedSamples,
        IReadOnlyList<MessageSummary> messageSummaries,
        DecoderDiagnostics diagnostics,
        ImportReport? importReport = null,
        DatasetCompleteness completeness = DatasetCompleteness.Complete,
        string sourceLogSha256 = "",
        string dbcSha256 = "",
        string applicationVersion = "",
        DateTimeOffset? startTimeUtc = null)
    {
        var counts = new Dictionary<SignalIdentity, int>();
        if (decodedSamples is ISignalSampleLookup lookup)
        {
            foreach (var summary in lookup.GetSignalSummaries()) counts.Add(summary.Latest.Identity, summary.Count);
        }
        else
        {
            foreach (var sample in decodedSamples)
                counts[sample.Identity] = counts.TryGetValue(sample.Identity, out var count) ? checked(count + 1) : 1;
        }

        var seriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>();
        var seriesByLabel = new Dictionary<string, SignalSeries>(StringComparer.Ordinal);
        foreach (var pair in counts.OrderBy(static pair => pair.Key.DisplayLabel, StringComparer.Ordinal))
        {
            var identity = pair.Key;
            var expectedCount = pair.Value;
            var series = new SignalSeries(
                identity,
                expectedCount,
                cancellationToken => LoadSeries(decodedSamples, identity, expectedCount, cancellationToken),
                maximumPoints => LoadSampledSeries(decodedSamples, identity, expectedCount, maximumPoints));
            seriesByIdentity.Add(pair.Key, series);
            seriesByLabel.Add(series.Label, series);
        }

        var labels = seriesByLabel.Keys.ToList();
        IReadOnlyList<DecodedSignalSample> samplesForDataset = completeness == DatasetCompleteness.Partial
            ? new DecodedSampleQualityView(decodedSamples, DecodeQuality.PartialDataset)
            : decodedSamples;

        return new CanDataset
        {
            RawFrames = rawFrames,
            DecodedSamples = samplesForDataset,
            MessageSummaries = messageSummaries
                .OrderByDescending(summary => summary.Count)
                .ThenBy(summary => summary.FrameId)
                .ToList(),
            SignalLabels = labels,
            SignalSeriesByLabel = seriesByLabel,
            SignalSeriesByIdentity = seriesByIdentity,
            Diagnostics = diagnostics,
            ImportReport = importReport,
            Completeness = completeness,
            SourceLogSha256 = sourceLogSha256,
            DbcSha256 = dbcSha256,
            ApplicationVersion = applicationVersion,
            StartTimeUtc = startTimeUtc?.ToUniversalTime()
        };
    }

    private static (long[] Timestamps, double[] Values) LoadSeries(
        IReadOnlyList<DecodedSignalSample> decodedSamples,
        SignalIdentity identity,
        int expectedCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new SeriesBuffer(expectedCount, cancellationToken);
        var visited = 0;
        if (decodedSamples is ISignalSampleLookup lookup)
        {
            foreach (var point in lookup.ReadSignalSeries(identity))
            {
                if ((visited++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                buffer.Append(point);
            }
        }
        else
        {
            foreach (var sample in decodedSamples)
            {
                if ((visited++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (sample.Identity == identity)
                    buffer.Append(new SignalSeriesPoint(sample.TimestampNanoseconds, sample.FrameIndex, sample.Value));
            }
        }

        return buffer.Complete(cancellationToken);
    }

    private static (long[] Timestamps, double[] Values) LoadSampledSeries(
        IReadOnlyList<DecodedSignalSample> decodedSamples,
        SignalIdentity identity,
        int expectedCount,
        int maximumPoints)
    {
        if (expectedCount <= maximumPoints) return LoadSeries(decodedSamples, identity, expectedCount);
        var buffer = new SeriesBuffer(maximumPoints);
        if (decodedSamples is ISignalSampleLookup lookup)
        {
            foreach (var point in lookup.ReadSignalSeriesSampled(identity, maximumPoints)) buffer.Append(point);
        }
        else
        {
            var target = 0L;
            var outputIndex = 0;
            var sourceIndex = 0L;
            foreach (var sample in decodedSamples)
            {
                if (sample.Identity != identity) continue;
                if (sourceIndex == target)
                {
                    buffer.Append(new SignalSeriesPoint(sample.TimestampNanoseconds, sample.FrameIndex, sample.Value));
                    outputIndex++;
                    if (outputIndex >= maximumPoints) break;
                    target = (long)outputIndex * (expectedCount - 1L) / (maximumPoints - 1L);
                }

                sourceIndex++;
            }
        }

        return buffer.Complete();
    }

    private sealed class SeriesBuffer
    {
        private readonly long[] _timestamps;
        private readonly long[] _frameIndices;
        private readonly double[] _values;
        private int _index;

        public SeriesBuffer(int count, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _timestamps = new long[count];
            cancellationToken.ThrowIfCancellationRequested();
            _frameIndices = new long[count];
            cancellationToken.ThrowIfCancellationRequested();
            _values = new double[count];
        }

        public void Append(SignalSeriesPoint sample)
        {
            _timestamps[_index] = sample.TimestampNanoseconds;
            _frameIndices[_index] = sample.FrameIndex;
            _values[_index] = sample.Value;
            _index++;
        }

        public (long[] Timestamps, double[] Values) Complete(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 1; i < _timestamps.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (_timestamps[i] > _timestamps[i - 1] ||
                    (_timestamps[i] == _timestamps[i - 1] && _frameIndices[i] >= _frameIndices[i - 1]))
                {
                    continue;
                }

                var order = new int[_timestamps.Length];
                for (var index = 0; index < order.Length; index++)
                {
                    if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    order[index] = index;
                }
                CancellableArrayOperations.Sort(order, (left, right) =>
                {
                    var timestampOrder = _timestamps[left].CompareTo(_timestamps[right]);
                    if (timestampOrder != 0) return timestampOrder;
                    var frameOrder = _frameIndices[left].CompareTo(_frameIndices[right]);
                    return frameOrder != 0 ? frameOrder : left.CompareTo(right);
                }, cancellationToken);
                var sortedTimestamps = new long[_timestamps.Length];
                cancellationToken.ThrowIfCancellationRequested();
                var sortedValues = new double[_values.Length];
                for (var target = 0; target < order.Length; target++)
                {
                    if ((target & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    sortedTimestamps[target] = _timestamps[order[target]];
                    sortedValues[target] = _values[order[target]];
                }

                cancellationToken.ThrowIfCancellationRequested();
                return (sortedTimestamps, sortedValues);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (_timestamps, _values);
        }
    }
}
