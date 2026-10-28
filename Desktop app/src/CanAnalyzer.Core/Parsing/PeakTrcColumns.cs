using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

/// <summary>PEAK 2.x column names are case-sensitive: L is DLC, l is payload length.</summary>
internal sealed class PeakTrcColumns
{
    private static readonly int[] PayloadLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];
    private readonly string[] _columns;
    private readonly Dictionary<string, int> _indices;

    public PeakTrcColumns(string header)
    {
        _columns = header.Split(',', StringSplitOptions.TrimEntries);
        if (_columns.Distinct(StringComparer.Ordinal).Count() != _columns.Length ||
            _columns.Length == 0 || _columns[^1] != "D" ||
            _columns.Any(column => column is not ("N" or "O" or "T" or "B" or "I" or "d" or "R" or "l" or "L" or "D")) ||
            !new[] { "O", "T", "I", "d" }.All(_columns.Contains) ||
            _columns.Contains("L") == _columns.Contains("l"))
            throw new FormatException("De PEAK-kolomindeling wordt niet ondersteund.");
        _indices = _columns.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index);
    }

    public RawCanFrame? Parse(string line, long frameIndex, long lineNumber)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string Value(string name) => _indices.TryGetValue(name, out var index) && index < tokens.Length
            ? tokens[index] : throw new FormatException($"PEAK-kolom {name} ontbreekt.");
        var type = Value("T");
        if (type == "EV") return null; // A textual event is not a CAN frame.
        if (tokens.Length < _columns.Length - 1) throw new FormatException("Onvolledige PEAK-regel.");
        var timestamp = TimestampParsers.ParseDecimalMillisecondsToNanoseconds(Value("O"));
        var channel = _indices.ContainsKey("B") ? Value("B") : string.Empty;
        if (channel.Length != 0 && (!int.TryParse(channel, out var bus) || bus is < 1 or > 16))
            throw new FormatException("Ongeldig PEAK-busnummer.");
        var direction = Value("d") switch
        {
            "Rx" => CanFrameDirection.Receive,
            "Tx" => CanFrameDirection.Transmit,
            _ => throw new FormatException("Ongeldige PEAK-richting.")
        };
        var data = HexUtilities.ParseDataBytes(tokens.Skip(_columns.Length - 1).ToArray())
            ?? throw new FormatException("Payload bevat ongeldige hexbytes.");
        var declared = int.Parse(Value(_indices.ContainsKey("L") ? "L" : "l"), CultureInfo.InvariantCulture);
        var kind = type switch
        {
            "DT" or "FD" or "FB" or "FE" or "BI" => CanFrameKind.Data,
            "RR" => CanFrameKind.Remote,
            "ER" or "ST" or "EC" => CanFrameKind.Error,
            _ => throw new FormatException($"Niet-ondersteund PEAK-berichttype: {type}.")
        };
        var idText = Value("I");
        var id = kind == CanFrameKind.Error && idText == "-" ? 0u
            : uint.Parse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var extended = kind != CanFrameKind.Error && (idText.Length == 8 || id > 0x7FF);
        var fd = type is "FD" or "FB" or "FE" or "BI";
        if (kind != CanFrameKind.Remote && _indices.ContainsKey("L"))
        {
            if (declared < 0 || declared > (fd ? 15 : 8) || data.Length != PayloadLengths[declared])
                throw new FormatException("PEAK-payloadlengte komt niet overeen met DLC-kolom L.");
        }
        if (!fd && kind == CanFrameKind.Data && data.Length > 8)
            throw new FormatException("Een klassiek PEAK-CAN-frame bevat meer dan 8 bytes.");
        if (_indices.ContainsKey("l") && kind != CanFrameKind.Remote && data.Length != declared)
            throw new FormatException("PEAK-payloadlengte komt niet overeen met kolom l.");
        byte dlc;
        CanFrameFormat format;
        if (kind == CanFrameKind.Remote)
        {
            if (declared is < 0 or > 8 || data.Length != 0 || id > (extended ? 0x1FFFFFFFu : 0x7FFu))
                throw new FormatException("Ongeldig PEAK-RTR-frame.");
            dlc = (byte)declared;
            format = CanFrameFormat.Classic;
        }
        else if (!CanFrameValidation.TryNormalize(id, extended, declared, data.Length, fd,
                     out dlc, out format, out var error))
            throw new FormatException(error);
        return new(timestamp, id, dlc, data, type, channel, extended, frameIndex, lineNumber,
            format, direction, kind, type is "FB" or "BI", type is "FE" or "BI");
    }
}
