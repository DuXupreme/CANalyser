using System.Globalization;
using System.Text.RegularExpressions;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Decoding;

/// <inheritdoc />
public sealed partial class DbcLoader : IDbcLoader
{
    [GeneratedRegex(
        "^BO_\\s+(?<id>\\d+)\\s+(?<name>[^:\\s]+)\\s*:\\s*(?<dlc>\\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MessageRegex();

    [GeneratedRegex(
        "^(?<start>\\d+)\\|(?<length>\\d+)@(?<byteorder>[01])(?<sign>[+-])\\s+\\((?<scale>[-+0-9.eE]+),(?<offset>[-+0-9.eE]+)\\)\\s+\\[(?<min>[-+0-9.eE]+)\\|(?<max>[-+0-9.eE]+)\\]\\s+\"(?<unit>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex SignalTailRegex();

    public async Task<DbcDatabase> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        var messages = new List<DbcMessage>();
        DbcMessage? currentMessage = null;

        foreach (var rawLine in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("CM_", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("BO_", StringComparison.Ordinal))
            {
                if (TryParseMessage(line, out var message))
                {
                    messages.Add(message);
                    currentMessage = message;
                }

                continue;
            }

            if (!line.StartsWith("SG_", StringComparison.Ordinal) || currentMessage is null)
            {
                continue;
            }

            if (TryParseSignal(line, out var signal))
            {
                currentMessage.Signals.Add(signal);
            }
        }

        return new DbcDatabase { Messages = messages };
    }

    private static bool TryParseMessage(string line, out DbcMessage message)
    {
        var match = MessageRegex().Match(line);
        if (!match.Success)
        {
            message = default!;
            return false;
        }

        var rawFrameId = uint.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
        var name = match.Groups["name"].Value.Trim();
        var dlc = int.Parse(match.Groups["dlc"].Value, CultureInfo.InvariantCulture);

        var isExtended =
            (rawFrameId & CanIdUtilities.DbcExtendedFlag) != 0 ||
            rawFrameId > CanIdUtilities.CanExtendedMask ||
            rawFrameId > 0x7FF;

        message = new DbcMessage
        {
            RawFrameId = rawFrameId,
            Name = name.Length == 0 ? "<unnamed>" : name,
            Dlc = dlc,
            IsExtendedFrame = isExtended
        };
        return true;
    }

    private static bool TryParseSignal(string line, out DbcSignal signal)
    {
        signal = default!;
        var payload = line[3..].Trim();
        var parts = payload.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var leftTokens = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (leftTokens.Length == 0)
        {
            return false;
        }

        var signalName = leftTokens[0];
        var isMultiplexer = leftTokens.Skip(1).Any(token => token.Equals("M", StringComparison.Ordinal));
        var muxIds = new List<int>();

        foreach (var token in leftTokens.Skip(1))
        {
            if (!token.StartsWith('m') || token.Length < 2)
            {
                continue;
            }

            var numeric = new string(token.Skip(1).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                muxIds.Add(id);
            }
        }

        var match = SignalTailRegex().Match(parts[1].Trim());
        if (!match.Success)
        {
            return false;
        }

        signal = new DbcSignal
        {
            Name = signalName,
            StartBit = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture),
            Length = int.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture),
            IsLittleEndian = match.Groups["byteorder"].Value == "1",
            IsSigned = match.Groups["sign"].Value == "-",
            Scale = ParseDouble(match.Groups["scale"].Value, 1d),
            Offset = ParseDouble(match.Groups["offset"].Value, 0d),
            Minimum = ParseDouble(match.Groups["min"].Value, 0d),
            Maximum = ParseDouble(match.Groups["max"].Value, 0d),
            Unit = match.Groups["unit"].Value,
            IsMultiplexer = isMultiplexer,
            MultiplexerIds = muxIds
        };

        return true;
    }

    private static double ParseDouble(string text, double fallback)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return fallback;
    }
}
