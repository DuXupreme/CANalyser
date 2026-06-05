using System.Text.RegularExpressions;

namespace CanAnalyzer.Core.Parsing;

internal static partial class ParserRegex
{
    [GeneratedRegex(
        "^(?<ts>\\d{1,2}:\\d{1,2}:\\d{1,2}:\\d{1,4})\\s+(?<dir>Rx|Tx)\\s+(?<channel>\\d+)\\s+(?<id>0x[0-9A-Fa-f]+|[0-9A-Fa-f]+)\\s+(?<frame_type>[A-Za-z]+)\\s+(?<dlc>\\d+)\\s*(?<data>(?:[0-9A-Fa-f]{2}\\s*){0,64})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    public static partial Regex BusmasterLine();

    [GeneratedRegex(
        "^\\((?<ts>\\d+(?:\\.\\d+)?)\\)\\s+\\S+\\s+(?<id>[0-9A-Fa-f]+)#(?<data>[0-9A-Fa-f]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    public static partial Regex Candump();

    [GeneratedRegex(
        "^\\s*(?<msgno>\\d+\\))?\\s*(?<time_ms>\\d+(?:[\\.,]\\d+)?)\\s+(?<dir>Rx|Tx|DT|FD)?\\s*(?<id>[0-9A-Fa-f]+)\\s+(?<dlc>\\d+)\\s+(?<data>(?:[0-9A-Fa-f]{2}\\s*){0,64})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    public static partial Regex PeakTrcClassic();

    [GeneratedRegex(
        "^\\s*(?<time_ms>\\d+(?:[\\.,]\\d+)?)\\t(?<channel>[^\\t]*)\\t(?<unknown>[^\\t]*)\\t(?<id>[0-9A-Fa-f]+)\\t(?<dlc>\\d+)\\t(?<data>(?:[0-9A-Fa-f]{2}(?:\\s+[0-9A-Fa-f]{2})*)?)\\t(?<tail>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    public static partial Regex PeakTrcTsv();
}
