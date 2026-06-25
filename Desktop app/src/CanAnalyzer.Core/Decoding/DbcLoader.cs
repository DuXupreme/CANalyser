using System.Globalization;
using System.Text.RegularExpressions;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;
using DbcParserLib;
using DbcParserLib.Model;
using DbcParserLib.Observers;

namespace CanAnalyzer.Core.Decoding;

/// <summary>DBC loader backed by DbcParserLib with explicit failure capture and range augmentation.</summary>
public sealed partial class DbcLoader : IDbcLoader
{
    private static readonly object ParserLock = new();

    [GeneratedRegex(@"SG_MUL_VAL_\s+(?<id>\d+)\s+(?<signal>\w+)\s+(?<mux>\w+)\s+(?<ranges>(?:\d+-\d+(?:,\s*)?)+)\s*;?", RegexOptions.CultureInvariant)]
    private static partial Regex ExtendedMuxRegex();

    [GeneratedRegex(@"^BO_\s+(?<id>\d+)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MessageRegex();

    [GeneratedRegex(@"^\s*SG_\s+(?<signal>\w+)(?:\s+(?<mux>M|m\d+))?\s*:\s*(?<start>\d+)\|(?<length>\d+)@(?<order>[01])(?<signed>[+-])\s+\((?<scale>[^,]+),(?<offset>[^)]+)\)\s+\[(?<minimum>[^|]+)\|(?<maximum>[^\]]+)\]\s+""(?<unit>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex SignalDefinitionRegex();

    [GeneratedRegex(@"^SIG_VALTYPE_\s+(?<id>\d+)\s+(?<signal>\w+)\s*:\s*(?<kind>\d+)\s*;?", RegexOptions.CultureInvariant)]
    private static partial Regex SignalValueTypeRegex();

    [GeneratedRegex(@"at line (?<line>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ObserverLineRegex();

    public async Task<DbcDatabase> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var observer = new SimpleFailureObserver();
        Dbc parsed;
        lock (ParserLock)
        {
            Parser.SetParsingFailuresObserver(observer);
            parsed = Parser.Parse(text);
        }

        var issues = observer.GetErrorList()
            .Select(error => ToIssue(error, text))
            .ToList();
        var messages = parsed.Messages.Select(MapMessage).ToList();
        ApplySimpleMultiplexing(text, messages);
        ApplyExtendedMultiplexing(text, messages, issues);
        ValidateSignalLayouts(messages, issues);

        if (messages.Count == 0)
        {
            issues.Add(new ImportIssue(ImportIssueSeverity.Error, "DBC_NO_MESSAGES", "DbcParserLib", 0,
                "DBC bevat geen bruikbare berichten.", string.Empty));
        }

        return new DbcDatabase
        {
            Messages = messages,
            Issues = issues,
            // The editor domain does not preserve comments/attributes verbatim. Imported files are therefore read-only.
            IsLosslessWritable = false
        };
    }

    private static DbcMessage MapMessage(Message source)
    {
        var rawId = source.IsExtID ? source.ID | CanIdUtilities.DbcExtendedFlag : source.ID;
        var message = new DbcMessage
        {
            RawFrameId = rawId,
            IsExtendedFrame = source.IsExtID,
            Name = string.IsNullOrWhiteSpace(source.Name) ? "<unnamed>" : source.Name,
            Dlc = source.DLC
        };
        foreach (var signal in source.Signals)
        {
            var mux = signal.MultiplexingInfo();
            message.Signals.Add(new DbcSignal
            {
                Name = signal.Name,
                StartBit = signal.StartBit,
                Length = signal.Length,
                IsLittleEndian = signal.ByteOrder != 0,
                IsSigned = signal.ValueType == DbcValueType.Signed,
                Scale = signal.Factor,
                Offset = signal.Offset,
                Minimum = signal.Minimum,
                Maximum = signal.Maximum,
                Unit = signal.Unit ?? string.Empty,
                IsMultiplexer = mux.Role == MultiplexingRole.Multiplexor,
                MultiplexerIds = mux.Role == MultiplexingRole.Multiplexed ? [mux.Group] : [],
                ValueKind = signal.ValueType switch
                {
                    DbcValueType.IEEEFloat => DbcSignalValueKind.IeeeFloat32,
                    DbcValueType.IEEEDouble => DbcSignalValueKind.IeeeFloat64,
                    _ => DbcSignalValueKind.Integer
                }
            });
        }
        return message;
    }

    private static void ApplyExtendedMultiplexing(string text, IReadOnlyList<DbcMessage> messages, ICollection<ImportIssue> issues)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var match = ExtendedMuxRegex().Match(lines[index]);
            if (!match.Success) continue;
            try
            {
                var diskId = uint.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
                var normalized = CanIdUtilities.NormalizeDbcFrameId(diskId, (diskId & CanIdUtilities.DbcExtendedFlag) != 0);
                var message = messages.FirstOrDefault(item => item.NormalizedFrameId == normalized);
                var signal = message?.Signals.FirstOrDefault(item => item.Name == match.Groups["signal"].Value);
                if (signal is null) throw new FormatException("Extended multiplexed signal or message was not found.");
                var ranges = new List<DbcMultiplexerRange>();
                foreach (var rangeText in match.Groups["ranges"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var bounds = rangeText.Split('-', 2);
                    var min = uint.Parse(bounds[0], CultureInfo.InvariantCulture);
                    var max = uint.Parse(bounds[1], CultureInfo.InvariantCulture);
                    if (max < min) throw new FormatException("Multiplexer range maximum is below minimum.");
                    ranges.Add(new DbcMultiplexerRange(match.Groups["mux"].Value, min, max));
                }
                // DbcSignal is a class with init properties; replace it in the owning message to attach immutable ranges.
                var position = message!.Signals.IndexOf(signal);
                message.Signals[position] = CloneWithRanges(signal, ranges);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                issues.Add(new ImportIssue(ImportIssueSeverity.Error, "DBC_EXT_MUX", "DbcParserLib", index + 1, ex.Message, lines[index]));
            }
        }
    }

    private static void ApplySimpleMultiplexing(string text, IReadOnlyList<DbcMessage> messages)
    {
        var valueKinds = ParseSignalValueKinds(text);
        var rawSignals = ParseRawSignalDefinitions(text, valueKinds);
        foreach (var message in messages)
        {
            if (!rawSignals.TryGetValue(message.RawFrameId, out var definitions))
            {
                continue;
            }

            message.Signals.Clear();
            message.Signals.AddRange(definitions.Select(static definition => definition.ToSignal()));
        }
    }

    private static IReadOnlyDictionary<(uint RawMessageId, string SignalName), DbcSignalValueKind> ParseSignalValueKinds(string text)
    {
        var result = new Dictionary<(uint RawMessageId, string SignalName), DbcSignalValueKind>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = SignalValueTypeRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var rawId = uint.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            var signalName = match.Groups["signal"].Value;
            var kind = int.Parse(match.Groups["kind"].Value, CultureInfo.InvariantCulture) switch
            {
                1 => DbcSignalValueKind.IeeeFloat32,
                2 => DbcSignalValueKind.IeeeFloat64,
                _ => DbcSignalValueKind.Integer
            };
            result[(rawId, signalName)] = kind;
        }

        return result;
    }

    private static IReadOnlyDictionary<uint, IReadOnlyList<RawSignalDefinition>> ParseRawSignalDefinitions(
        string text,
        IReadOnlyDictionary<(uint RawMessageId, string SignalName), DbcSignalValueKind> valueKinds)
    {
        var result = new Dictionary<uint, List<RawSignalDefinition>>();
        uint? currentRawId = null;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var messageMatch = MessageRegex().Match(line);
            if (messageMatch.Success)
            {
                currentRawId = uint.Parse(messageMatch.Groups["id"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (!currentRawId.HasValue)
            {
                continue;
            }

            var signalMatch = SignalDefinitionRegex().Match(line);
            if (!signalMatch.Success)
            {
                continue;
            }

            var signalName = signalMatch.Groups["signal"].Value;
            var mux = signalMatch.Groups["mux"].Success ? signalMatch.Groups["mux"].Value : string.Empty;
            if (!result.TryGetValue(currentRawId.Value, out var list))
            {
                list = [];
                result[currentRawId.Value] = list;
            }

            list.Add(new RawSignalDefinition(
                signalName,
                int.Parse(signalMatch.Groups["start"].Value, CultureInfo.InvariantCulture),
                int.Parse(signalMatch.Groups["length"].Value, CultureInfo.InvariantCulture),
                signalMatch.Groups["order"].Value == "1",
                signalMatch.Groups["signed"].Value == "-",
                double.Parse(signalMatch.Groups["scale"].Value, NumberStyles.Float, CultureInfo.InvariantCulture),
                double.Parse(signalMatch.Groups["offset"].Value, NumberStyles.Float, CultureInfo.InvariantCulture),
                double.Parse(signalMatch.Groups["minimum"].Value, NumberStyles.Float, CultureInfo.InvariantCulture),
                double.Parse(signalMatch.Groups["maximum"].Value, NumberStyles.Float, CultureInfo.InvariantCulture),
                signalMatch.Groups["unit"].Value,
                mux.Equals("M", StringComparison.Ordinal),
                mux.StartsWith('m') ? [int.Parse(mux[1..], CultureInfo.InvariantCulture)] : [],
                valueKinds.TryGetValue((currentRawId.Value, signalName), out var kind) ? kind : DbcSignalValueKind.Integer));
        }

        return result.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<RawSignalDefinition>)pair.Value);
    }

    private static DbcSignal CloneWithRanges(DbcSignal signal, IReadOnlyList<DbcMultiplexerRange> ranges) => new()
    {
        Name = signal.Name,
        StartBit = signal.StartBit,
        Length = signal.Length,
        IsLittleEndian = signal.IsLittleEndian,
        IsSigned = signal.IsSigned,
        Scale = signal.Scale,
        Offset = signal.Offset,
        Minimum = signal.Minimum,
        Maximum = signal.Maximum,
        Unit = signal.Unit,
        IsMultiplexer = signal.IsMultiplexer,
        MultiplexerIds = signal.MultiplexerIds,
        MultiplexerRanges = ranges,
        ValueKind = signal.ValueKind
    };

    private sealed record RawSignalDefinition(
        string Name,
        int StartBit,
        int Length,
        bool IsLittleEndian,
        bool IsSigned,
        double Scale,
        double Offset,
        double Minimum,
        double Maximum,
        string Unit,
        bool IsMultiplexer,
        IReadOnlyList<int> MultiplexerIds,
        DbcSignalValueKind ValueKind)
    {
        public DbcSignal ToSignal() => new()
        {
            Name = Name,
            StartBit = StartBit,
            Length = Length,
            IsLittleEndian = IsLittleEndian,
            IsSigned = IsSigned,
            Scale = Scale,
            Offset = Offset,
            Minimum = Minimum,
            Maximum = Maximum,
            Unit = Unit,
            IsMultiplexer = IsMultiplexer,
            MultiplexerIds = MultiplexerIds,
            ValueKind = ValueKind
        };
    }

    private static void ValidateSignalLayouts(IEnumerable<DbcMessage> messages, ICollection<ImportIssue> issues)
    {
        foreach (var message in messages)
        {
            var invalidReason = FindBlockingSignalLayoutIssue(message);
            if (invalidReason is null)
            {
                continue;
            }

            message.SuppressDecoding = true;
            issues.Add(new ImportIssue(
                ImportIssueSeverity.Error,
                "DBC_SIGNAL_LAYOUT",
                "DBC semantic validator",
                0,
                $"{message.Name} (0x{message.NormalizedFrameId:X}) wordt niet gedecodeerd: {invalidReason}",
                string.Empty));
        }
    }

    private static string? FindBlockingSignalLayoutIssue(DbcMessage message)
    {
        var payloadBits = message.Dlc * 8;
        var occupied = new List<HashSet<int>>(message.Signals.Count);
        foreach (var signal in message.Signals)
        {
            var bits = DbcBitLayout.GetOccupiedLsb0Bits(signal.StartBit, signal.Length, signal.IsLittleEndian);
            if (bits.Count != signal.Length)
            {
                return $"signal {signal.Name} heeft een ongeldige bitlengte.";
            }

            if (bits.Any(bit => bit < 0 || bit >= payloadBits))
            {
                return $"signal {signal.Name} valt buiten de {message.Dlc}-byte payload.";
            }

            occupied.Add(bits.ToHashSet());
        }

        for (var i = 0; i < message.Signals.Count; i++)
        {
            var left = message.Signals[i];
            for (var j = i + 1; j < message.Signals.Count; j++)
            {
                var right = message.Signals[j];
                if (!SignalsCanBeActiveTogether(left, right))
                {
                    continue;
                }

                if (occupied[i].Overlaps(occupied[j]))
                {
                    return $"signals {left.Name} en {right.Name} overlappen in hetzelfde actieve muxpad.";
                }
            }
        }

        return null;
    }

    private static bool SignalsCanBeActiveTogether(DbcSignal left, DbcSignal right)
    {
        if (left.IsMultiplexer || right.IsMultiplexer)
        {
            return true;
        }

        var leftIntervals = GetMuxIntervals(left);
        var rightIntervals = GetMuxIntervals(right);
        if (leftIntervals.Count == 0 || rightIntervals.Count == 0)
        {
            return true;
        }

        return leftIntervals.Any(leftInterval =>
            rightIntervals.Any(rightInterval =>
                leftInterval.Minimum <= rightInterval.Maximum &&
                rightInterval.Minimum <= leftInterval.Maximum));
    }

    private static IReadOnlyList<(uint Minimum, uint Maximum)> GetMuxIntervals(DbcSignal signal)
    {
        if (signal.MultiplexerRanges.Count > 0)
        {
            return signal.MultiplexerRanges
                .Select(static range => (range.Minimum, range.Maximum))
                .ToList();
        }

        if (signal.MultiplexerIds.Count > 0)
        {
            return signal.MultiplexerIds
                .Where(static id => id >= 0)
                .Select(static id => ((uint)id, (uint)id))
                .ToList();
        }

        return [];
    }

    private static ImportIssue ToIssue(string error, string source)
    {
        var lineMatch = ObserverLineRegex().Match(error);
        var line = lineMatch.Success ? long.Parse(lineMatch.Groups["line"].Value, CultureInfo.InvariantCulture) : 0;
        var sourceLine = line > 0 ? source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ElementAtOrDefault((int)line - 1) ?? string.Empty : string.Empty;
        return new ImportIssue(ImportIssueSeverity.Error, "DBC_PARSE", "DbcParserLib", line, error, sourceLine);
    }
}
