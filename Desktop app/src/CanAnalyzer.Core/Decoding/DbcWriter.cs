using System.Globalization;
using System.Text;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Decoding;

/// <inheritdoc />
public sealed class DbcWriter : IDbcWriter
{
    private const string NewLine = "\r\n";

    // Standard new-symbol block so external DBC tools (Vector CANdb++, Kvaser, PCAN) accept the file.
    private static readonly string[] NewSymbols =
    [
        "NS_DESC_", "CM_", "BA_DEF_", "BA_", "VAL_", "CAT_DEF_", "CAT_", "FILTER",
        "BA_DEF_DEF_", "EV_DATA_", "ENVVAR_DATA_", "SGTYPE_", "SGTYPE_VAL_",
        "BA_DEF_SGTYPE_", "BA_SGTYPE_", "SIG_TYPE_REF_", "VAL_TABLE_", "SIG_GROUP_",
        "SIG_VALTYPE_", "SIGTYPE_VALTYPE_", "BO_TX_BU_", "BA_DEF_REL_", "BA_REL_",
        "BA_DEF_DEF_REL_", "BU_SG_REL_", "BU_EV_REL_", "BU_BO_REL_", "SG_MUL_VAL_"
    ];

    public async Task WriteAsync(DbcDatabase database, string filePath, CancellationToken cancellationToken)
    {
        var content = Serialize(database);
        await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
    }

    public string Serialize(DbcDatabase database)
    {
        var builder = new StringBuilder();

        builder.Append("VERSION \"\"").Append(NewLine).Append(NewLine);

        builder.Append("NS_ :").Append(NewLine);
        foreach (var symbol in NewSymbols)
        {
            builder.Append('\t').Append(symbol).Append(NewLine);
        }

        builder.Append(NewLine);
        builder.Append("BS_:").Append(NewLine).Append(NewLine);
        builder.Append("BU_:").Append(NewLine).Append(NewLine);

        foreach (var message in database.Messages)
        {
            AppendMessage(builder, message);
        }

        return builder.ToString();
    }

    private static void AppendMessage(StringBuilder builder, DbcMessage message)
    {
        var diskId = message.IsExtendedFrame
            ? message.NormalizedFrameId | CanIdUtilities.DbcExtendedFlag
            : message.NormalizedFrameId;

        var name = SanitizeIdentifier(message.Name, "Message");
        var dlc = Math.Clamp(message.Dlc, 0, 64);

        builder
            .Append("BO_ ")
            .Append(diskId.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(name)
            .Append(": ")
            .Append(dlc.ToString(CultureInfo.InvariantCulture))
            .Append(" Vector__XXX")
            .Append(NewLine);

        foreach (var signal in message.Signals)
        {
            AppendSignal(builder, signal);
        }

        builder.Append(NewLine);
    }

    private static void AppendSignal(StringBuilder builder, DbcSignal signal)
    {
        var name = SanitizeIdentifier(signal.Name, "Signal");
        var mux = signal.IsMultiplexer
            ? " M"
            : signal.MultiplexerIds.Count > 0
                ? " m" + signal.MultiplexerIds[0].ToString(CultureInfo.InvariantCulture)
                : string.Empty;

        var byteOrder = signal.IsLittleEndian ? '1' : '0';
        var sign = signal.IsSigned ? '-' : '+';

        builder
            .Append(" SG_ ")
            .Append(name)
            .Append(mux)
            .Append(" : ")
            .Append(signal.StartBit.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(signal.Length.ToString(CultureInfo.InvariantCulture))
            .Append('@')
            .Append(byteOrder)
            .Append(sign)
            .Append(" (")
            .Append(FormatNumber(signal.Scale))
            .Append(',')
            .Append(FormatNumber(signal.Offset))
            .Append(") [")
            .Append(FormatNumber(signal.Minimum))
            .Append('|')
            .Append(FormatNumber(signal.Maximum))
            .Append("] \"")
            .Append(EscapeUnit(signal.Unit))
            .Append("\" Vector__XXX")
            .Append(NewLine);
    }

    private static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string EscapeUnit(string? unit)
    {
        if (string.IsNullOrEmpty(unit))
        {
            return string.Empty;
        }

        // DBC string literals cannot contain raw double quotes; drop them to keep the file valid.
        return unit.Replace("\"", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Forces a name into a valid DBC identifier: [A-Za-z_][A-Za-z0-9_]*.
    /// </summary>
    private static string SanitizeIdentifier(string? raw, string fallbackPrefix)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallbackPrefix;
        }

        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        var cleaned = builder.ToString();
        if (cleaned.Length == 0)
        {
            return fallbackPrefix;
        }

        if (!char.IsLetter(cleaned[0]) && cleaned[0] != '_')
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }
}
