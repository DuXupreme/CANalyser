using System.Buffers.Binary;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Analysis;

/// <summary>CANedge 01.09 internal DBC; only reads channels identified by the importer as internal.</summary>
public static class LoggerDiagnosticReader
{
    private const long EpochOffset = 1577840400;
    private const int MaximumClockObservations = 100_000;

    public static LoggerDiagnostics Read(IReadOnlyList<RawCanFrame> frames,
        IReadOnlyList<string> internalChannels, CancellationToken token = default)
    {
        if (internalChannels.Count == 0) return new();
        var channels = internalChannels.ToHashSet(StringComparer.Ordinal);
        var observations = new List<LoggerClockObservation>();
        long count = 0, invalid = 0;
        var truncated = false;
        LoggerStorageObservation? storage = null;
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            if (!channels.Contains(frame.Channel)) continue;
            count++;
            if (frame.Kind != CanFrameKind.Data || frame.IsExtended) continue;
            var data = frame.Data.AsSpan();
            LoggerClockObservation? observation = null;
            switch (frame.Id)
            {
                case 2 when data.Length == 7:
                    storage = new(frame.TimestampNanoseconds,
                        Epoch(BinaryPrimitives.ReadUInt32LittleEndian(data[1..])),
                        BinaryPrimitives.ReadUInt16LittleEndian(data[5..]),
                        (data[0] & 4) != 0, (data[0] & 16) != 0);
                    break;
                case 3 when data.Length == 4:
                    var calendar = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    try
                    {
                        var time = new DateTimeOffset(2000 + (int)(calendar & 63), 1 + (int)((calendar >> 6) & 15),
                            1 + (int)((calendar >> 10) & 31), (int)((calendar >> 15) & 31),
                            (int)((calendar >> 20) & 63), (int)((calendar >> 26) & 63), TimeSpan.Zero);
                        observation = new(frame.TimestampNanoseconds, "calendar", time);
                    }
                    catch (ArgumentOutOfRangeException) { invalid++; }
                    break;
                case 5 when data.Length == 8:
                    observation = new(frame.TimestampNanoseconds, "external-received",
                        Epoch(BinaryPrimitives.ReadUInt32LittleEndian(data)),
                        Epoch(BinaryPrimitives.ReadUInt32LittleEndian(data[4..])));
                    break;
                case 2 or 3 or 5:
                    invalid++;
                    break;
            }
            if (observation is null) continue;
            if (observations.Count < MaximumClockObservations) observations.Add(observation);
            else truncated = true; // Raw frames remain available, including every omitted observation.
        }
        return new() { InternalFrameCount = count, ClockObservations = observations,
            LastStorageObservation = storage, ClockObservationsTruncated = truncated, InvalidInternalMessageCount = invalid };
    }

    private static DateTimeOffset Epoch(uint seconds) => DateTimeOffset.FromUnixTimeSeconds(EpochOffset + seconds);
}
