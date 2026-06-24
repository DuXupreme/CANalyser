using System.Globalization;
using System.Numerics;
using System.Text;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
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
        var decodedSamples = new List<DecodedSignalSample>(rawFrames.Count * 2);
        var summaryCounts = new Dictionary<(uint FrameId, string MessageName), int>();
        var unmatchedCounter = new Dictionary<uint, int>();
        var manualDecodeCounter = new Dictionary<uint, int>();

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
        for (var i = 0; i < rawFrames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i % reportStride == 0)
            {
                var percent = Math.Clamp(20 + (int)Math.Round((i / (double)Math.Max(1, rawFrames.Count)) * 60.0), 20, 80);
                progress?.Report(new LoadProgress($"DBC decode: frame {i:N0} / {rawFrames.Count:N0}", percent));
            }

            var frame = rawFrames[i];
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
                    candidates.AddRange(pgnMatches);
                }
            }

            if (candidates.Count == 0)
            {
                Count(unmatchedCounter, rawFrameId);
                continue;
            }

            Dictionary<string, DecodedSignalValue>? decoded = null;
            DbcMessage? decodedMessage = null;
            var manualUsed = false;

            foreach (var message in candidates)
            {
                if (message.IsExtendedFrame != isExtended)
                {
                    continue;
                }

                if (TryDecodeMessage(message, frame.Data, permissive: false, out var strict))
                {
                    decoded = strict;
                    decodedMessage = message;
                    manualUsed = false;
                    break;
                }

                if (TryDecodeMessage(message, frame.Data, permissive: true, out var permissiveResult))
                {
                    decoded = permissiveResult;
                    decodedMessage = message;
                    manualUsed = true;
                    break;
                }
            }

            if (decodedMessage is null || decoded is null)
            {
                Count(unmatchedCounter, rawFrameId);
                continue;
            }

            if (manualUsed)
            {
                Count(manualDecodeCounter, rawFrameId);
            }

            Count(summaryCounts, (normalizedFrameId, decodedMessage.Name));

            foreach (var pair in decoded)
            {
                if (double.IsNaN(pair.Value.PhysicalValue) || double.IsInfinity(pair.Value.PhysicalValue))
                {
                    continue;
                }

                decodedSamples.Add(
                    new DecodedSignalSample(
                        TimeSeconds: (float)frame.TimeSeconds,
                        FrameId: normalizedFrameId,
                        MessageName: decodedMessage.Name,
                        SignalName: pair.Key,
                        Value: (float)pair.Value.PhysicalValue,
                        RawValueHex: FormatRawValue(pair.Value.RawValue),
                        Unit: pair.Value.Unit));
            }
        }

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
            ManualDecodeFrameCount: manualDecodeCounter.Values.Sum(),
            ManualDecodeUniqueIds: manualDecodeCounter.Count,
            DecodeNote: BuildDecodeDiagnostics(
                rawFrames,
                database,
                unmatchedCounter,
                decodedSamples.Count,
                exactMap,
                manualDecodeCounter));

        progress?.Report(new LoadProgress("Decode klaar.", 90));
        return new DecodeResult(decodedSamples, summaries, diagnostics);
    }

    private static bool TryDecodeMessage(
        DbcMessage message,
        byte[] data,
        bool permissive,
        out Dictionary<string, DecodedSignalValue> decoded)
    {
        decoded = new Dictionary<string, DecodedSignalValue>(StringComparer.Ordinal);
        var multiplexerValues = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var signal in message.Signals.Where(s => s.IsMultiplexer))
        {
            if (!TryDecodeSignalValue(signal, data, permissive, out var value))
            {
                if (!permissive)
                {
                    return false;
                }

                continue;
            }

            decoded[signal.Name] = value;
            multiplexerValues[signal.Name] = (int)Math.Round(value.PhysicalValue);
        }

        foreach (var signal in message.Signals.Where(s => !s.IsMultiplexer))
        {
            if (signal.MultiplexerIds.Count > 0)
            {
                var matched = multiplexerValues.Values.Any(muxValue => signal.MultiplexerIds.Contains(muxValue));
                if (!matched)
                {
                    continue;
                }
            }

            if (!TryDecodeSignalValue(signal, data, permissive, out var value))
            {
                if (!permissive)
                {
                    return false;
                }

                continue;
            }

            decoded[signal.Name] = value;
        }

        return decoded.Count > 0;
    }

    private static bool TryDecodeSignalValue(
        DbcSignal signal,
        byte[] data,
        bool permissive,
        out DecodedSignalValue value)
    {
        value = default;
        if (!TryExtractRaw(signal, data, permissive, out var raw))
        {
            return false;
        }

        value = new DecodedSignalValue(
            ApplySignalScaling(raw, signal),
            raw,
            signal.Unit);
        return true;
    }

    private static string FormatRawValue(BigInteger rawValue)
    {
        var hex = rawValue
            .ToString("X", CultureInfo.InvariantCulture)
            .TrimStart('0');
        return $"0x{(hex.Length == 0 ? "0" : hex)}";
    }

    private static bool TryExtractRaw(DbcSignal signal, byte[] data, bool permissive, out BigInteger rawValue)
    {
        rawValue = BigInteger.Zero;
        if (signal.Length <= 0)
        {
            return true;
        }

        if (signal.IsLittleEndian)
        {
            // BigInteger reads little-endian two's complement; add a zero byte to force unsigned interpretation.
            var unsignedBytes = new byte[data.Length + 1];
            Buffer.BlockCopy(data, 0, unsignedBytes, 0, data.Length);
            var payload = new BigInteger(unsignedBytes);

            if (!permissive && (signal.StartBit + signal.Length > data.Length * 8))
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
            if (!TryGetBitLsb0(data, bitIndex, permissive, out var bit))
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

    private static bool TryGetBitLsb0(byte[] data, int bitIndex, bool permissive, out BigInteger bit)
    {
        var byteIndex = bitIndex / 8;
        var bitInByte = bitIndex % 8;
        if (byteIndex < 0 || byteIndex >= data.Length)
        {
            if (!permissive)
            {
                bit = BigInteger.Zero;
                return false;
            }

            bit = BigInteger.Zero;
            return true;
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
        Dictionary<uint, int> manualDecodeCounter)
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
            $"Permissief/handmatig gedecodeerde frames: {manualDecodeCounter.Values.Sum()}",
            $"Permissief/handmatig gedecodeerde unieke IDs: {manualDecodeCounter.Count}",
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

        if (manualDecodeCounter.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Frames die alleen via permissieve fallback decodeerden:");
            foreach (var pair in manualDecodeCounter
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
