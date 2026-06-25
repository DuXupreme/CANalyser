using System.Numerics;
using System.Text;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Decoding;

/// <inheritdoc />
public sealed class CanDecodingService : ICanDecodingService
{
    public DecodeResult Decode(
        IReadOnlyList<RawCanFrame> rawFrames,
        DbcDatabase database,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var decodedSamples = new DiskBackedDecodedSampleStore();
        var summaryCounts = new Dictionary<(uint FrameId, string MessageName), int>();
        var unmatchedCounter = new Dictionary<uint, int>();
        var decodeErrorCounter = new Dictionary<uint, int>();
        var ambiguousCounter = new Dictionary<uint, int>();

        var exactMap = new Dictionary<(bool IsExtended, uint FrameId), List<DbcMessage>>();
        var pgnToMessages = new Dictionary<uint, List<DbcMessage>>();

        foreach (var message in database.Messages)
        {
            var normalized = message.NormalizedFrameId;
            var key = (message.IsExtendedFrame, normalized);
            if (!exactMap.TryGetValue(key, out var list))
            {
                list = [];
                exactMap[key] = list;
            }

            list.Add(message);

            if (message.IsExtendedFrame)
            {
                var pgn = CanIdUtilities.ExtractJ1939Pgn(normalized);
                if (pgn.HasValue)
                {
                    if (!pgnToMessages.TryGetValue(pgn.Value, out var pgnList))
                    {
                        pgnList = [];
                        pgnToMessages[pgn.Value] = pgnList;
                    }

                    pgnList.Add(message);
                }
            }
        }

        var reportStride = Math.Max(500, rawFrames.Count / 100);
        var i = 0;
        foreach (var frame in rawFrames)
        {
            var frameNumber = i++;
            cancellationToken.ThrowIfCancellationRequested();
            if (frameNumber % reportStride == 0)
            {
                var percent = Math.Clamp(20 + (int)Math.Round((frameNumber / (double)Math.Max(1, rawFrames.Count)) * 60.0), 20, 80);
                progress?.Report(new LoadProgress($"DBC decode: frame {frameNumber:N0} / {rawFrames.Count:N0}", percent));
            }

            var rawFrameId = frame.Id;
            var isExtended = frame.IsExtended || rawFrameId > 0x7FF;
            var normalizedFrameId = CanIdUtilities.NormalizeDbcFrameId(rawFrameId, isExtended);

            var candidates = new List<DbcMessage>();
            if (exactMap.TryGetValue((isExtended, normalizedFrameId), out var exact))
            {
                candidates.AddRange(exact);
            }
            else if (isExtended)
            {
                var pgn = CanIdUtilities.ExtractJ1939Pgn(normalizedFrameId);
                if (pgn.HasValue && pgnToMessages.TryGetValue(pgn.Value, out var pgnMatches))
                {
                    if (pgnMatches.Count == 1)
                    {
                        candidates.Add(pgnMatches[0]);
                    }
                    else
                    {
                        Count(ambiguousCounter, rawFrameId);
                        continue;
                    }
                }
            }

            if (candidates.Count == 0)
            {
                Count(unmatchedCounter, rawFrameId);
                continue;
            }

            Dictionary<string, DecodedSignalValue>? decoded = null;
            DbcMessage? decodedMessage = null;
            foreach (var message in candidates)
            {
                if (message.SuppressDecoding)
                {
                    continue;
                }

                if (message.IsExtendedFrame != isExtended)
                {
                    continue;
                }

                if (message.Dlc != frame.PayloadLength)
                {
                    continue;
                }

                if (TryDecodeMessage(message, frame.Data, out var strict))
                {
                    decoded = strict;
                    decodedMessage = message;
                    break;
                }
            }

            if (decodedMessage is null || decoded is null)
            {
                Count(decodeErrorCounter, rawFrameId);
                continue;
            }

            Count(summaryCounts, (normalizedFrameId, decodedMessage.Name));

            foreach (var pair in decoded)
            {
                if (double.IsNaN(pair.Value.PhysicalValue) || double.IsInfinity(pair.Value.PhysicalValue))
                {
                    continue;
                }

                decodedSamples.Append(
                    new DecodedSignalSample(
                        TimestampNanoseconds: frame.TimestampNanoseconds,
                        FrameIndex: frame.FrameIndex,
                        SourceLineNumber: frame.SourceLineNumber,
                        Identity: new SignalIdentity(frame.Channel, frame.FrameFormat, isExtended, normalizedFrameId, decodedMessage.Name, pair.Key),
                        Value: pair.Value.PhysicalValue,
                        RawValue: pair.Value.RawValue,
                        Unit: pair.Value.Unit));
            }

        }

        decodedSamples.Complete();

        progress?.Report(new LoadProgress("Bouw berichtsamenvatting...", 85));

        var summaries = summaryCounts
            .Select(pair => new MessageSummary(pair.Key.FrameId, pair.Key.MessageName, pair.Value))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.FrameId)
            .ToList();

        var diagnostics = new DecoderDiagnostics(
            UnmatchedFrameCount: unmatchedCounter.Values.Sum(),
            UnmatchedUniqueIds: unmatchedCounter.Count,
            DbcMessageCount: database.Messages.Count,
            ManualDecodeFrameCount: 0,
            ManualDecodeUniqueIds: 0,
            DecodeNote: BuildDecodeDiagnostics(
                rawFrames,
                database,
                unmatchedCounter,
                decodedSamples.Count,
                exactMap,
                decodeErrorCounter),
            DecodeErrorFrameCount: decodeErrorCounter.Values.Sum(),
            AmbiguousFrameCount: ambiguousCounter.Values.Sum());

        progress?.Report(new LoadProgress("Decode klaar.", 90));
        return new DecodeResult(decodedSamples, summaries, diagnostics);
    }

    private static bool TryDecodeMessage(
        DbcMessage message,
        byte[] data,
        out Dictionary<string, DecodedSignalValue> decoded)
    {
        decoded = new Dictionary<string, DecodedSignalValue>(StringComparer.Ordinal);
        if (message.Signals.Count == 0)
        {
            return true;
        }

        var multiplexerValues = new Dictionary<string, BigInteger>(StringComparer.Ordinal);

        foreach (var signal in message.Signals.Where(s => s.IsMultiplexer))
        {
            if (!TryDecodeSignalValue(signal, data, out var value))
            {
                return false;
            }

            decoded[signal.Name] = value;
            multiplexerValues[signal.Name] = value.RawValue;
        }

        foreach (var signal in message.Signals.Where(s => !s.IsMultiplexer))
        {
            if (signal.MultiplexerIds.Count > 0)
            {
                var matched = multiplexerValues.Values.Any(muxValue =>
                    muxValue >= int.MinValue && muxValue <= int.MaxValue && signal.MultiplexerIds.Contains((int)muxValue));
                if (!matched)
                {
                    continue;
                }
            }

            if (signal.MultiplexerRanges.Count > 0)
            {
                var matched = signal.MultiplexerRanges.Any(range =>
                    multiplexerValues.TryGetValue(range.MultiplexerSignalName, out var muxValue) &&
                    muxValue >= range.Minimum && muxValue <= range.Maximum);
                if (!matched) continue;
            }

            if (!TryDecodeSignalValue(signal, data, out var value))
            {
                return false;
            }

            decoded[signal.Name] = value;
        }

        return decoded.Count > 0;
    }

    private static bool TryDecodeSignalValue(
        DbcSignal signal,
        byte[] data,
        out DecodedSignalValue value)
    {
        value = default;
        if (!TryExtractRaw(signal, data, out var raw))
        {
            return false;
        }

        value = new DecodedSignalValue(
            ApplySignalScaling(raw, signal),
            raw,
            signal.Unit);
        return true;
    }

    private static bool TryExtractRaw(DbcSignal signal, byte[] data, out BigInteger rawValue)
    {
        rawValue = BigInteger.Zero;
        if (signal.Length <= 0)
        {
            return false;
        }

        if (signal.IsLittleEndian)
        {
            // BigInteger reads little-endian two's complement; add a zero byte to force unsigned interpretation.
            var unsignedBytes = new byte[data.Length + 1];
            Buffer.BlockCopy(data, 0, unsignedBytes, 0, data.Length);
            var payload = new BigInteger(unsignedBytes);

            if (signal.StartBit < 0 || signal.StartBit + signal.Length > data.Length * 8)
            {
                return false;
            }

            var mask = (BigInteger.One << signal.Length) - BigInteger.One;
            rawValue = (payload >> signal.StartBit) & mask;
            return true;
        }

        var raw = BigInteger.Zero;
        foreach (var bitIndex in DbcBitLayout.GetOccupiedLsb0Bits(signal.StartBit, signal.Length, isLittleEndian: false))
        {
            if (!TryGetBitLsb0(data, bitIndex, out var bit))
            {
                return false;
            }

            raw = (raw << 1) | bit;
        }

        rawValue = raw;
        return true;
    }

    private static double ApplySignalScaling(BigInteger rawValue, DbcSignal signal)
    {
        if (signal.ValueKind == DbcSignalValueKind.IeeeFloat32)
        {
            if (signal.Length != 32 || rawValue < uint.MinValue || rawValue > uint.MaxValue) return double.NaN;
            var rawFloat = BitConverter.Int32BitsToSingle(unchecked((int)(uint)rawValue));
            return (rawFloat * signal.Scale) + signal.Offset;
        }

        if (signal.ValueKind == DbcSignalValueKind.IeeeFloat64)
        {
            if (signal.Length != 64 || rawValue < ulong.MinValue || rawValue > ulong.MaxValue) return double.NaN;
            var rawDouble = BitConverter.Int64BitsToDouble(unchecked((long)(ulong)rawValue));
            return (rawDouble * signal.Scale) + signal.Offset;
        }

        var value = rawValue;
        if (signal.IsSigned && signal.Length > 0)
        {
            var signBit = BigInteger.One << (signal.Length - 1);
            if ((value & signBit) != BigInteger.Zero)
            {
                value -= BigInteger.One << signal.Length;
            }
        }

        var asDouble = (double)value;
        return (asDouble * signal.Scale) + signal.Offset;
    }

    private static bool TryGetBitLsb0(byte[] data, int bitIndex, out BigInteger bit)
    {
        var byteIndex = bitIndex / 8;
        var bitInByte = bitIndex % 8;
        if (byteIndex < 0 || byteIndex >= data.Length)
        {
            bit = BigInteger.Zero;
            return false;
        }

        bit = (data[byteIndex] >> bitInByte) & 1;
        return true;
    }

    private static string BuildDecodeDiagnostics(
        IReadOnlyList<RawCanFrame> rawFrames,
        DbcDatabase database,
        Dictionary<uint, int> unmatchedCounter,
        int decodedRowsCount,
        Dictionary<(bool IsExtended, uint FrameId), List<DbcMessage>> exactMap,
        Dictionary<uint, int> decodeErrorCounter)
    {
        var totalFrames = rawFrames.Count;
        var uniqueRawIds = rawFrames.Select(frame => frame.Id).Distinct().Count();
        var unmatchedTotal = unmatchedCounter.Values.Sum();
        var unmatchedUnique = unmatchedCounter.Count;

        var stdMessages = database.Messages.Where(message => !message.IsExtendedFrame).ToList();
        var extMessages = database.Messages.Where(message => message.IsExtendedFrame).ToList();

        var lines = new List<string>
        {
            $"DBC berichten: {database.Messages.Count}",
            $"Ruwe frames: {totalFrames}",
            $"Unieke raw IDs: {uniqueRawIds}",
            $"Gedecodeerde meetpunten: {decodedRowsCount}",
            $"Niet-gematchte frames: {unmatchedTotal}",
            $"Niet-gematchte unieke IDs: {unmatchedUnique}",
            $"Frames met decode-/lengtefout: {decodeErrorCounter.Values.Sum()}",
            $"Unieke IDs met decode-/lengtefout: {decodeErrorCounter.Count}",
            $"DBC standaard berichten: {stdMessages.Count}",
            $"DBC extended berichten: {extMessages.Count}"
        };

        if (unmatchedCounter.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Top onbekende frame IDs uit de log:");
            foreach (var pair in unmatchedCounter
                         .OrderByDescending(kv => kv.Value)
                         .ThenBy(kv => kv.Key)
                         .Take(12))
            {
                var frameId = pair.Key;
                var count = pair.Value;
                var isExtended = frameId > 0x7FF;
                var normalized = CanIdUtilities.NormalizeDbcFrameId(frameId, isExtended);
                var pgn = isExtended ? CanIdUtilities.ExtractJ1939Pgn(normalized) : null;
                var pgnText = pgn.HasValue ? $" | PGN 0x{pgn.Value:X}" : string.Empty;
                if (exactMap.TryGetValue((isExtended, normalized), out var known))
                {
                    var names = string.Join(", ", known.Take(4).Select(message => message.Name));
                    lines.Add($"- 0x{frameId:X} : {count}x | exact genormaliseerde match aanwezig -> {names}{pgnText}");
                }
                else
                {
                    lines.Add($"- 0x{frameId:X} : {count}x | genormaliseerd=0x{normalized:X} | {(isExtended ? "extended" : "standard")}{pgnText}");
                }
            }
        }

        if (decodeErrorCounter.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Frames die strict niet konden worden gedecodeerd:");
            foreach (var pair in decodeErrorCounter
                         .OrderByDescending(kv => kv.Value)
                         .ThenBy(kv => kv.Key)
                         .Take(12))
            {
                lines.Add($"- 0x{pair.Key:X} : {pair.Value}x");
            }
        }

        if (stdMessages.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Voorbeeld standaard DBC IDs (genormaliseerd):");
            foreach (var message in stdMessages
                         .OrderBy(msg => msg.NormalizedFrameId)
                         .Take(12))
            {
                lines.Add(DescribeDbcMessage(message));
            }
        }

        if (extMessages.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Voorbeeld extended DBC IDs (genormaliseerd):");
            foreach (var message in extMessages
                         .OrderBy(msg => msg.NormalizedFrameId)
                         .Take(12))
            {
                lines.Add(DescribeDbcMessage(message));
            }
        }

        if (decodedRowsCount == 0)
        {
            lines.Add(string.Empty);
            lines.Add("Geen enkel DBC-signaal kon worden gedecodeerd. Dat is nu niet meer blokkerend: raw frames blijven gewoon geladen.");
        }

        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine();
            }

            builder.Append(lines[i]);
        }

        return builder.ToString();
    }

    private static string DescribeDbcMessage(DbcMessage message)
    {
        var rawId = message.RawFrameId;
        var normalized = message.NormalizedFrameId;
        var pgn = message.IsExtendedFrame ? CanIdUtilities.ExtractJ1939Pgn(normalized) : null;
        var pgnText = pgn.HasValue ? $" | PGN 0x{pgn.Value:X}" : string.Empty;
        return $"- {message.Name} : raw=0x{rawId:X} -> norm=0x{normalized:X} | {(message.IsExtendedFrame ? "extended" : "standard")}{pgnText}";
    }

    private static void Count<TKey>(Dictionary<TKey, int> dictionary, TKey key)
        where TKey : notnull
    {
        if (dictionary.TryGetValue(key, out var count))
        {
            dictionary[key] = count + 1;
        }
        else
        {
            dictionary[key] = 1;
        }
    }

    private readonly record struct DecodedSignalValue(
        double PhysicalValue,
        BigInteger RawValue,
        string Unit);
}
