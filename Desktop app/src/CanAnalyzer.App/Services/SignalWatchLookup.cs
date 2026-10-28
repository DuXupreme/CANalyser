using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.App.Services;

/// <summary>Indexed previous-value lookup. Raw values always come from the exact source frame.</summary>
public sealed class SignalWatchLookup(CanDataset dataset)
{
    private Dictionary<SignalIdentity, DecodedSignalSample[]>? _memory;

    public DecodedSignalSample? AtOrBefore(SignalIdentity identity, long timestamp)
    {
        if (dataset.DecodedSamples is ISignalSampleLookup lookup && dataset.DecodedSamples is IFrameSampleLookup frames)
        {
            var point = lookup.FindSignalPointAtOrBefore(identity, timestamp);
            return point is { } p ? frames.GetFrameSamples(p.FrameIndex)
                .LastOrDefault(sample => sample.Identity == identity && sample.TimestampNanoseconds == p.TimestampNanoseconds) : null;
        }
        _memory ??= dataset.DecodedSamples.GroupBy(sample => sample.Identity).ToDictionary(group => group.Key,
            group => group.OrderBy(sample => sample.TimestampNanoseconds).ThenBy(sample => sample.FrameIndex).ToArray());
        if (!_memory.TryGetValue(identity, out var samples)) return null;
        var left = 0; var right = samples.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (samples[middle].TimestampNanoseconds <= timestamp) left = middle + 1; else right = middle;
        }
        return left == 0 ? null : samples[left - 1];
    }

    public string Status(SignalIdentity identity, DecodedSignalSample? sample, long timestamp)
    {
        if (sample is null) return "Nog geen sample vóór cursor";
        var cursor = timestamp / 1e9;
        var sampleTime = sample.TimeSeconds;
        foreach (var gap in MeasurementGapSelection.ForChannel(dataset.ImportReport, identity.Channel))
        {
            if (cursor > gap.StartSeconds && cursor < gap.EndSeconds) return "Meetgat: vorige waarde, niet actueel";
            if (gap.StartSeconds >= sampleTime && gap.EndSeconds <= cursor) return "Geen nieuwe sample na meetgat";
        }
        return sample.TimestampNanoseconds == timestamp ? "Sample op cursor" : "Vorige sample; zie leeftijd";
    }
}
