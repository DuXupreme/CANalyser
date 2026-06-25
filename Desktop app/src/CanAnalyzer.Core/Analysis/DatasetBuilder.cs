using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;

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
        string applicationVersion = "")
    {
        var counts = new Dictionary<SignalIdentity, int>();
        foreach (var sample in decodedSamples)
        {
            counts[sample.Identity] = counts.TryGetValue(sample.Identity, out var count) ? checked(count + 1) : 1;
        }

        var seriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>();
        var seriesByLabel = new Dictionary<string, SignalSeries>(StringComparer.Ordinal);
        foreach (var pair in counts.OrderBy(static pair => pair.Key.DisplayLabel, StringComparer.Ordinal))
        {
            var identity = pair.Key;
            var expectedCount = pair.Value;
            var series = new SignalSeries(identity, () => LoadSeries(decodedSamples, identity, expectedCount));
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
            ApplicationVersion = applicationVersion
        };
    }

    private static (long[] Timestamps, double[] Values) LoadSeries(
        IReadOnlyList<DecodedSignalSample> decodedSamples,
        SignalIdentity identity,
        int expectedCount)
    {
        var buffer = new SeriesBuffer(expectedCount);
        foreach (var sample in decodedSamples)
        {
            if (sample.Identity == identity) buffer.Append(sample);
        }

        return buffer.Complete();
    }

    private sealed class SeriesBuffer
    {
        private readonly long[] _timestamps;
        private readonly long[] _frameIndices;
        private readonly double[] _values;
        private int _index;

        public SeriesBuffer(int count)
        {
            _timestamps = new long[count];
            _frameIndices = new long[count];
            _values = new double[count];
        }

        public void Append(DecodedSignalSample sample)
        {
            _timestamps[_index] = sample.TimestampNanoseconds;
            _frameIndices[_index] = sample.FrameIndex;
            _values[_index] = sample.Value;
            _index++;
        }

        public (long[] Timestamps, double[] Values) Complete()
        {
            for (var i = 1; i < _timestamps.Length; i++)
            {
                if (_timestamps[i] > _timestamps[i - 1] ||
                    (_timestamps[i] == _timestamps[i - 1] && _frameIndices[i] >= _frameIndices[i - 1]))
                {
                    continue;
                }

                var order = Enumerable.Range(0, _timestamps.Length).ToArray();
                Array.Sort(order, (left, right) =>
                {
                    var timestampOrder = _timestamps[left].CompareTo(_timestamps[right]);
                    if (timestampOrder != 0) return timestampOrder;
                    var frameOrder = _frameIndices[left].CompareTo(_frameIndices[right]);
                    return frameOrder != 0 ? frameOrder : left.CompareTo(right);
                });
                var sortedTimestamps = new long[_timestamps.Length];
                var sortedValues = new double[_values.Length];
                for (var target = 0; target < order.Length; target++)
                {
                    sortedTimestamps[target] = _timestamps[order[target]];
                    sortedValues[target] = _values[order[target]];
                }

                return (sortedTimestamps, sortedValues);
            }

            return (_timestamps, _values);
        }
    }
}
