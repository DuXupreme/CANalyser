using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

public sealed partial class AnalysisRunService
{
    /// <summary>Detailed traffic statistics from original frames, with the same scope and evidence gates as the standard plan.</summary>
    public ModuleResult<CanDetailResult> AnalyzeCanDetail(CanDataset dataset, AnalysisRunRequest request,
        string channel, double binWidthSeconds = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(channel);
        if (!double.IsFinite(binWidthSeconds) || binWidthSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(binWidthSeconds));
        if ((request.Modules & ~AnalysisModules.All) != 0)
            throw new ArgumentException("Onbekend analyseplan.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = dataset.AcquireReadLease();
        // Reuse request/profile validation, internal-channel exclusion, range selection and coverage policy.
        var run = Analyze(dataset, request with { Modules = request.Modules & AnalysisModules.Can }, cancellationToken);
        var requestedBinWidth = Math.Clamp(binWidthSeconds, .05, 5);
        var basis = JsonSerializer.Serialize(new { run.DatasetFingerprint, run.Profile, run.Scope,
            run.StartSeconds, run.EndSeconds, Channel = channel, BinWidthSeconds = requestedBinWidth });
        var resultId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis + ":can.detail:2"))).ToLowerInvariant();
        if ((request.Modules & AnalysisModules.Can) == 0)
            return Wrap(NotRequested<CanChannelResult>("can"), null);
        var summary = run.CanChannels.FirstOrDefault(item => item.SubjectId == channel);
        if (summary is null)
        {
            var internalChannel = dataset.ImportReport?.InternalDiagnosticChannels.Contains(channel, StringComparer.OrdinalIgnoreCase) == true;
            return Wrap(Missing<CanChannelResult>("can", channel,
                internalChannel ? "internal_diagnostic_channel" : "no_frames_in_scope",
                internalChannel ? "Interne loggerdiagnostiek is geen machine-CAN-verkeer."
                    : "Geen machine-CAN-frames op dit kanaal binnen het analysebereik.", [], ModuleStatus.InsufficientCoverage)
                with { Coverage = new(Math.Max(0, run.EndSeconds - run.StartSeconds), 0, 0) }, null);
        }
        if (summary.Value is not { } traffic) return Wrap(summary, null);
        try
        {
            var duration = traffic.EndSeconds - traffic.StartSeconds;
            var requiredBins = Math.Max(1L, checked((long)Math.Ceiling(duration / requestedBinWidth) + 1));
            var binCount = (int)Math.Min(200_000L, requiredBins);
            var actualWidth = requiredBins > 200_000 ? duration / 199_999d : requestedBinWidth;
            var counts = new long[binCount];
            var transmission = new double[binCount];
            var streams = new Dictionary<CanDetailStreamKey, CanDetailAccumulator>();
            var gaps = MeasurementGapSelection.Union(MeasurementGapSelection.ForChannel(dataset.ImportReport, channel));
            long extended = 0;
            var totalTransmission = 0d;
            foreach (var frame in dataset.RawFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Channel != channel || frame.TimeSeconds < run.StartSeconds || frame.TimeSeconds > run.EndSeconds) continue;
                var bin = Math.Clamp((int)((frame.TimeSeconds - traffic.StartSeconds) / actualWidth), 0, binCount - 1);
                counts[bin]++;
                if (frame.IsExtended) extended++;
                if (traffic.BitratesConfirmed)
                {
                    var seconds = EstimateCanDetailFrameSeconds(frame, run.Profile.CanBitrateKbps * 1000, run.Profile.CanDataBitrateKbps * 1000);
                    transmission[bin] += seconds;
                    totalTransmission += seconds;
                }
                var key = new CanDetailStreamKey(frame.Channel, frame.Direction, frame.FrameFormat, frame.IsExtended, frame.Id);
                if (!streams.TryGetValue(key, out var accumulator)) streams.Add(key, accumulator = new(key));
                accumulator.Observe(frame, gaps);
            }
            var bins = new CanDetailBin[binCount];
            var gapIndex = 0;
            double? peakRate = null, peakLoad = null;
            for (var index = 0; index < binCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = Math.Min(traffic.EndSeconds, traffic.StartSeconds + index * actualWidth);
                var to = Math.Min(traffic.EndSeconds, traffic.StartSeconds + (index + 1) * actualWidth);
                var covered = Math.Max(0, to - from);
                while (gapIndex < gaps.Count && gaps[gapIndex].EndSeconds <= from) gapIndex++;
                for (var gi = gapIndex; gi < gaps.Count && gaps[gi].StartSeconds < to; gi++)
                    covered -= Math.Max(0, Math.Min(to, gaps[gi].EndSeconds) - Math.Max(from, gaps[gi].StartSeconds));
                covered = Math.Max(0, covered);
                double? rate = covered > 0 ? counts[index] / covered : null;
                double? load = traffic.BitratesConfirmed && covered > 0 ? 100 * transmission[index] / covered : null;
                if (rate is { } r) peakRate = peakRate is { } previousRate ? Math.Max(previousRate, r) : r;
                if (load is { } l) peakLoad = peakLoad is { } previousLoad ? Math.Max(previousLoad, l) : l;
                bins[index] = new(from, to, covered, counts[index], traffic.BitratesConfirmed ? transmission[index] : null, rate, load);
            }
            var result = new CanDetailResult(channel, traffic.StartSeconds, traffic.EndSeconds, duration,
                traffic.FrameCount, extended, traffic.ErrorFrames, traffic.RemoteFrames,
                streams.Keys.Select(key => key.FrameId).Distinct().Count(), traffic.BitratesConfirmed ? totalTransmission : null,
                traffic.FramesPerSecond, peakRate, traffic.EstimatedBusLoadPercent, peakLoad, traffic.BitratesConfirmed,
                run.Profile.CanBitrateKbps, run.Profile.CanDataBitrateKbps, actualWidth,
                streams.OrderBy(pair => pair.Key.FrameId).ThenBy(pair => pair.Key.FrameFormat).ThenBy(pair => pair.Key.IsExtended)
                    .ThenBy(pair => pair.Key.Direction).Select(pair => pair.Value.Result()).ToArray(), bins);
            return Wrap(summary, result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or InvalidDataException)
        {
            return Wrap(summary with { Status = ModuleStatus.Failed, ReasonCodes = ["invalid_input"], Explanation = ex.Message }, null);
        }

        ModuleResult<CanDetailResult> Wrap(ModuleResult<CanChannelResult> source, CanDetailResult? value) => new()
        {
            ResultId = resultId, ModuleId = "can.detail", SubjectId = channel, MethodVersion = "2", Status = source.Status,
            Explanation = source.Explanation, ReasonCodes = source.ReasonCodes, Coverage = source.Coverage,
            Unit = "mixed", ValueUnits = new Dictionary<string, string>
            { ["frames"] = "count", ["cycle"] = "ms", ["jitter"] = "ms", ["rate"] = "frames/s", ["busLoad"] = "%", ["duration"] = "s", ["payload"] = "byte" },
            Value = value
        };
    }

    private static double EstimateCanDetailFrameSeconds(RawCanFrame frame, double arbitrationBitrate, double dataBitrate)
    {
        if (frame.FrameFormat == CanFrameFormat.Classic)
            return ((frame.IsExtended ? 67d : 47d) + frame.PayloadLength * 8d + 3d) * 1.20 / arbitrationBitrate;
        return (frame.IsExtended ? 41d : 23d) * 1.20 / arbitrationBitrate
            + (frame.PayloadLength * 8d + (frame.PayloadLength <= 16 ? 17d : 21d) + 13d) * 1.20
                / (frame.BitRateSwitch ? dataBitrate : arbitrationBitrate);
    }

    private sealed class CanDetailAccumulator(CanDetailStreamKey key)
    {
        private long _count, _payload, _cycles, _negative, _nonIncreasing, _gapCycles, _previousNanoseconds;
        private double _mean, _m2, _minimum = double.PositiveInfinity, _maximum = double.NegativeInfinity;
        public void Observe(RawCanFrame frame, IReadOnlyList<MeasurementGap> gaps)
        {
            if (_count++ > 0)
            {
                // Subtract integer timestamps before converting: tiny cycles remain visible at large logger offsets.
                var delta = (Int128)frame.TimestampNanoseconds - _previousNanoseconds;
                if (delta <= 0) { _nonIncreasing++; if (delta < 0) _negative++; }
                else if (CrossesGap(gaps, _previousNanoseconds / 1_000_000_000d, frame.TimeSeconds)) _gapCycles++;
                else
                {
                    var milliseconds = (double)delta / 1_000_000d;
                    _cycles++;
                    _minimum = Math.Min(_minimum, milliseconds); _maximum = Math.Max(_maximum, milliseconds);
                    var difference = milliseconds - _mean; _mean += difference / _cycles;
                    _m2 += difference * (milliseconds - _mean);
                }
            }
            _payload += frame.PayloadLength; _previousNanoseconds = frame.TimestampNanoseconds;
        }
        private static bool CrossesGap(IReadOnlyList<MeasurementGap> gaps, double from, double to)
        {
            var low = 0; var high = gaps.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (gaps[middle].EndSeconds <= from) low = middle + 1; else high = middle;
            }
            return low < gaps.Count && gaps[low].StartSeconds < to;
        }
        public CanDetailStream Result() => new(key, _count, _count > 0 ? _payload / (double)_count : 0,
            _cycles, _cycles > 0 ? _mean : null, _cycles > 0 ? _cycles > 1 ? Math.Sqrt(_m2 / (_cycles - 1)) : 0 : null,
            _cycles > 0 ? _minimum : null, _cycles > 0 ? _maximum : null, _negative, _nonIncreasing, _gapCycles);
    }
}
