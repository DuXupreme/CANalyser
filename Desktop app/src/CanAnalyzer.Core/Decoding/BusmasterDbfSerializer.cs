using System.Globalization;
using System.Text;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Decoding;

/// <summary>Reads and writes the BUSMASTER 1.3 text database format (.dbf).</summary>
public static class BusmasterDbfSerializer
{
    private const string ParserName = "BUSMASTER DBF";
    private const string NewLine = "\r\n";

    public static DbcDatabase Parse(string text)
    {
        var messages = new List<DbcMessage>();
        var issues = new List<ImportIssue>();
        DbcMessage? currentMessage = null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var sourceLine = lines[index];
            var line = sourceLine.Trim();
            if (line.StartsWith("[START_MSG]", StringComparison.OrdinalIgnoreCase))
            {
                currentMessage = ParseMessage(line, index + 1, sourceLine, issues);
                if (currentMessage is not null)
                {
                    messages.Add(currentMessage);
                }
            }
            else if (line.StartsWith("[START_SIGNALS]", StringComparison.OrdinalIgnoreCase))
            {
                if (currentMessage is null)
                {
                    AddIssue(issues, index + 1, "DBF_SIGNAL_WITHOUT_MESSAGE",
                        "Signaal staat buiten een bericht.", sourceLine);
                    continue;
                }

                var signal = ParseSignal(line, index + 1, sourceLine, issues);
                if (signal is not null)
                {
                    currentMessage.Signals.Add(signal);
                }
            }
            else if (line.StartsWith("[END_MSG]", StringComparison.OrdinalIgnoreCase))
            {
                currentMessage = null;
            }
        }

        if (messages.Count == 0)
        {
            AddIssue(issues, 0, "DBF_NO_MESSAGES", "DBF bevat geen bruikbare berichten.", string.Empty);
        }

        return new DbcDatabase
        {
            Messages = messages,
            Issues = issues,
            // Descriptions, value tables, nodes and custom parameters are not represented by the editor domain.
            IsLosslessWritable = false
        };
    }

    public static string Serialize(DbcDatabase database)
    {
        var builder = new StringBuilder();
        builder.Append("//******************************BUSMASTER Messages and signals Database ******************************//").Append(NewLine)
            .Append(NewLine)
            .Append("[DATABASE_VERSION] 1.3").Append(NewLine)
            .Append(NewLine)
            .Append("[PROTOCOL] CAN").Append(NewLine)
            .Append(NewLine)
            .Append("[BUSMASTER_VERSION] [3.2.2]").Append(NewLine)
            .Append("[NUMBER_OF_MESSAGES] ").Append(database.Messages.Count.ToString(CultureInfo.InvariantCulture)).Append(NewLine)
            .Append(NewLine);

        foreach (var message in database.Messages)
        {
            AppendMessage(builder, message);
        }

        builder.Append("[NODE] Vector__XXX").Append(NewLine)
            .Append(NewLine)
            .Append("[START_DESC]").Append(NewLine)
            .Append(NewLine)
            .Append("[START_DESC_MSG]").Append(NewLine)
            .Append("[END_DESC_MSG]").Append(NewLine)
            .Append("[START_DESC_NODE]").Append(NewLine)
            .Append("[END_DESC_NODE]").Append(NewLine)
            .Append("[START_DESC_SIG]").Append(NewLine)
            .Append("[END_DESC_SIG]").Append(NewLine)
            .Append("[END_DESC]").Append(NewLine)
            .Append(NewLine)
            .Append("[START_PARAM]").Append(NewLine)
            .Append("[START_PARAM_NET]").Append(NewLine)
            .Append("[END_PARAM_NET]").Append(NewLine)
            .Append("[START_PARAM_NODE]").Append(NewLine)
            .Append("[END_PARAM_NODE]").Append(NewLine)
            .Append("[START_PARAM_MSG]").Append(NewLine)
            .Append("[END_PARAM_MSG]").Append(NewLine)
            .Append("[START_PARAM_SIG]").Append(NewLine)
            .Append("[END_PARAM_SIG]").Append(NewLine)
            .Append("[START_PARAM_VAL]").Append(NewLine)
            .Append("[START_PARAM_NODE_VAL]").Append(NewLine)
            .Append("[END_PARAM_NODE_VAL]").Append(NewLine)
            .Append("[START_PARAM_MSG_VAL]").Append(NewLine)
            .Append("[END_PARAM_MSG_VAL]").Append(NewLine)
            .Append("[START_PARAM_SIG_VAL]").Append(NewLine)
            .Append("[END_PARAM_SIG_VAL]").Append(NewLine)
            .Append("[END_PARAM_VAL]").Append(NewLine);

        return builder.ToString();
    }

    private static DbcMessage? ParseMessage(
        string line,
        int lineNumber,
        string sourceLine,
        ICollection<ImportIssue> issues)
    {
        try
        {
            var values = line["[START_MSG]".Length..].Trim().Split(',');
            if (values.Length < 3)
            {
                throw new FormatException("Berichtregel bevat minder dan drie velden.");
            }

            var name = values[0].Trim();
            var frameId = uint.Parse(values[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var dlc = int.Parse(values[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var isExtended = values.Length > 5 && values[5].Trim().Equals("X", StringComparison.OrdinalIgnoreCase);
            if ((!isExtended && frameId > 0x7FF) ||
                (isExtended && frameId > CanIdUtilities.CanExtendedMask))
            {
                throw new FormatException($"CAN ID {frameId} valt buiten het bereik voor een {(isExtended ? "extended" : "standaard")} frame.");
            }

            if (dlc is < 0 or > 64)
            {
                throw new FormatException($"DLC {dlc} valt buiten het bereik 0-64.");
            }

            return new DbcMessage
            {
                RawFrameId = isExtended ? frameId | CanIdUtilities.DbcExtendedFlag : frameId,
                IsExtendedFrame = isExtended,
                Name = string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name,
                Dlc = dlc
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            AddIssue(issues, lineNumber, "DBF_MESSAGE", ex.Message, sourceLine);
            return null;
        }
    }

    private static DbcSignal? ParseSignal(
        string line,
        int lineNumber,
        string sourceLine,
        ICollection<ImportIssue> issues)
    {
        try
        {
            var values = line["[START_SIGNALS]".Length..].Trim().Split(',');
            if (values.Length < 12)
            {
                throw new FormatException("Signaalregel bevat minder dan twaalf velden.");
            }

            var name = values[0].Trim();
            var length = int.Parse(values[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var startByte = int.Parse(values[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var bitInByte = int.Parse(values[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            var kind = values[4].Trim().ToUpperInvariant();
            var rawMaximum = ParseNumber(values[5]);
            var rawMinimum = ParseNumber(values[6]);
            var isLittleEndian = values[7].Trim() == "1";
            var offset = ParseNumber(values[8]);
            var factor = ParseNumber(values[9]);
            if (factor == 0d)
            {
                factor = 1d;
            }

            if (length <= 0 || startByte <= 0 || bitInByte is < 0 or > 7)
            {
                throw new FormatException("Signaal heeft een ongeldige lengte of startpositie.");
            }

            var dbfStartBit = checked(((startByte - 1) * 8) + bitInByte);
            var startBit = isLittleEndian ? dbfStartBit : DbfMotorolaToDbcStartBit(dbfStartBit, length);
            var mux = values[11].Trim();
            var isMultiplexer = mux.Equals("M", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<int> muxIds = [];
            if (!isMultiplexer && mux.StartsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                muxIds = [int.Parse(mux[1..], NumberStyles.Integer, CultureInfo.InvariantCulture)];
            }

            return new DbcSignal
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Signal" : name,
                StartBit = startBit,
                Length = length,
                IsLittleEndian = isLittleEndian,
                IsSigned = kind == "I",
                Scale = factor,
                Offset = offset,
                Minimum = rawMinimum * factor,
                Maximum = rawMaximum * factor,
                Unit = values[10].Trim(),
                IsMultiplexer = isMultiplexer,
                MultiplexerIds = muxIds,
                ValueKind = kind switch
                {
                    "F" => DbcSignalValueKind.IeeeFloat32,
                    "D" => DbcSignalValueKind.IeeeFloat64,
                    _ => DbcSignalValueKind.Integer
                }
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            AddIssue(issues, lineNumber, "DBF_SIGNAL", ex.Message, sourceLine);
            return null;
        }
    }

    private static void AppendMessage(StringBuilder builder, DbcMessage message)
    {
        var frameFormat = message.IsExtendedFrame ? 'X' : 'S';
        builder.Append("[START_MSG] ")
            .Append(SanitizeField(message.Name, "Message")).Append(',')
            .Append(message.NormalizedFrameId.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(Math.Clamp(message.Dlc, 0, 64).ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(message.Signals.Count.ToString(CultureInfo.InvariantCulture))
            .Append(",0,").Append(frameFormat).Append(",Vector__XXX").Append(NewLine);

        foreach (var signal in message.Signals)
        {
            AppendSignal(builder, signal);
        }

        builder.Append("[END_MSG]").Append(NewLine).Append(NewLine);
    }

    private static void AppendSignal(StringBuilder builder, DbcSignal signal)
    {
        var dbfStartBit = signal.IsLittleEndian
            ? signal.StartBit
            : DbcMotorolaToDbfStartBit(signal.StartBit, signal.Length);
        var startByte = Math.DivRem(dbfStartBit, 8, out var bitInByte) + 1;
        var factor = signal.Scale == 0d ? 1d : signal.Scale;
        var sign = signal.ValueKind switch
        {
            DbcSignalValueKind.IeeeFloat32 => 'F',
            DbcSignalValueKind.IeeeFloat64 => 'D',
            _ => signal.IsSigned ? 'I' : 'U'
        };
        var mux = signal.IsMultiplexer
            ? "M"
            : signal.MultiplexerIds.Count > 0
                ? "m" + signal.MultiplexerIds[0].ToString(CultureInfo.InvariantCulture)
                : string.Empty;

        builder.Append("[START_SIGNALS] ")
            .Append(SanitizeField(signal.Name, "Signal")).Append(',')
            .Append(signal.Length.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(startByte.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(bitInByte.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(sign).Append(',')
            .Append(FormatNumber(signal.Maximum / factor)).Append(',')
            .Append(FormatNumber(signal.Minimum / factor)).Append(',')
            .Append(signal.IsLittleEndian ? '1' : '0').Append(',')
            .Append(FormatNumber(signal.Offset)).Append(',')
            .Append(FormatNumber(factor)).Append(',')
            .Append(SanitizeField(signal.Unit, string.Empty)).Append(',')
            .Append(mux).Append(',')
            .Append("Vector__XXX").Append(NewLine);
    }

    private static int DbfMotorolaToDbcStartBit(int dbfStartBit, int length)
    {
        var internalStart = checked(FlipBitNumbering(dbfStartBit) + 1 - length);
        if (internalStart < 0)
        {
            throw new FormatException("Motorola-signaal heeft een startpositie vóór het bericht.");
        }

        return FlipBitNumbering(internalStart);
    }

    private static int DbcMotorolaToDbfStartBit(int dbcStartBit, int length)
    {
        var internalStart = FlipBitNumbering(dbcStartBit);
        return FlipBitNumbering(checked(internalStart + length - 1));
    }

    private static int FlipBitNumbering(int bit) => bit - (bit % 8) + 7 - (bit % 8);

    private static double ParseNumber(string value) =>
        double.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string FormatNumber(double value) =>
        double.IsFinite(value) ? value.ToString("G17", CultureInfo.InvariantCulture) : "0";

    private static string SanitizeField(string? value, string fallback)
    {
        var sanitized = (value ?? string.Empty).Replace(",", "_", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return sanitized.Length == 0 ? fallback : sanitized;
    }

    private static void AddIssue(
        ICollection<ImportIssue> issues,
        long lineNumber,
        string code,
        string message,
        string sourceLine) =>
        issues.Add(new ImportIssue(ImportIssueSeverity.Error, code, ParserName, lineNumber, message, sourceLine));
}
