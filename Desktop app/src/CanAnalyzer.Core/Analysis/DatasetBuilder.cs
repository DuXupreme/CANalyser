using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.Core.Analysis;

/// <inheritdoc />
public sealed class DatasetBuilder : IDatasetBuilder
{
    public CanDataset Build(
        IReadOnlyList<RawCanFrame> rawFrames,
        IReadOnlyList<DecodedSignalSample> decodedSamples,
        IReadOnlyList<MessageSummary> messageSummaries,
        DecoderDiagnostics diagnostics)
    {
        var orderedSamples = decodedSamples
            .OrderBy(s => s.Label, StringComparer.Ordinal)
            .ThenBy(s => s.TimeSeconds)
            .ToList();

        var labels = orderedSamples
            .Select(sample => sample.Label)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToList();

        var seriesByLabel = new Dictionary<string, SignalSeries>(StringComparer.Ordinal);
        foreach (var group in orderedSamples.GroupBy(sample => sample.Label, StringComparer.Ordinal))
        {
            var x = group.Select(sample => sample.TimeSeconds).ToArray();
            var y = group.Select(sample => sample.Value).ToArray();
            seriesByLabel[group.Key] = new SignalSeries(group.Key, x, y);
        }

        return new CanDataset
        {
            RawFrames = rawFrames,
            DecodedSamples = orderedSamples,
            MessageSummaries = messageSummaries
                .OrderByDescending(summary => summary.Count)
                .ThenBy(summary => summary.FrameId)
                .ToList(),
            SignalLabels = labels,
            SignalSeriesByLabel = seriesByLabel,
            Diagnostics = diagnostics
        };
    }
}
