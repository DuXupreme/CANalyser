using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Parsing;

/// <summary>
/// Observed silence, not an assertion of packet loss or idle machine operation.
/// The timestamps remain unchanged. Internal traffic can bound but cannot fill a machine gap.
/// </summary>
internal sealed class MeasurementCoverage(IReadOnlyCollection<string> internalChannels)
{
    internal const long SilenceThresholdNanoseconds = 1_000_000_000;
    private readonly Dictionary<string, Coverage> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Coverage _machine = new();
    private long? _first;
    private long _last;

    public void Add(RawCanFrame frame)
    {
        _first ??= frame.TimestampNanoseconds;
        _last = frame.TimestampNanoseconds;
        if (frame.Kind != CanFrameKind.Data) return;
        if (!_channels.TryGetValue(frame.Channel, out var channel))
            _channels.Add(frame.Channel, channel = new Coverage());
        channel.Add(frame.TimestampNanoseconds);
        if (!internalChannels.Contains(frame.Channel, StringComparer.OrdinalIgnoreCase))
            _machine.Add(frame.TimestampNanoseconds);
    }

    public ImportReport Apply(ImportReport report)
    {
        var first = _first ?? 0;
        return report with
        {
            Gaps = _machine.Finish(first, _last),
            GapsByChannel = _channels.ToDictionary(pair => pair.Key,
                pair => pair.Value.Finish(first, _last), StringComparer.OrdinalIgnoreCase),
            InternalDiagnosticChannels = internalChannels.ToArray()
        };
    }

    private sealed class Coverage
    {
        private long? _first;
        private long _last;
        private readonly List<MeasurementGap> _gaps = [];

        public void Add(long timestamp)
        {
            if (_first.HasValue && timestamp - _last > SilenceThresholdNanoseconds)
                _gaps.Add(new(_last / 1e9, timestamp / 1e9));
            _first ??= timestamp;
            _last = timestamp;
        }

        public IReadOnlyList<MeasurementGap> Finish(long start, long end)
        {
            if (!_first.HasValue)
                return end - start > SilenceThresholdNanoseconds ? [new(start / 1e9, end / 1e9)] : [];
            var result = new List<MeasurementGap>(_gaps.Count + 2);
            if (_first.Value - start > SilenceThresholdNanoseconds) result.Add(new(start / 1e9, _first.Value / 1e9));
            result.AddRange(_gaps);
            if (end - _last > SilenceThresholdNanoseconds) result.Add(new(_last / 1e9, end / 1e9));
            return result;
        }
    }
}
