using System.Globalization;
using System.Text.RegularExpressions;

namespace CanAnalyzer.Core.Utilities;

/// <summary>
/// Timestamp parsing helpers for supported CAN log formats.
/// </summary>
public static partial class TimestampParsers
{
    [GeneratedRegex("^(?<day>\\d{2})T(?<hmsms>\\d{9})$", RegexOptions.Compiled)]
    private static partial Regex CssTsRegex();

    [GeneratedRegex("^(?<hh>\\d{1,2}):(?<mm>\\d{1,2}):(?<ss>\\d{1,2}):(?<frac>\\d{1,4})$", RegexOptions.Compiled)]
    private static partial Regex BusmasterTsRegex();

    public static double ParseCssTimestampToSeconds(string value)
    {
        var match = CssTsRegex().Match(value.Trim());
        if (!match.Success)
        {
            throw new FormatException($"Unsupported CSS timestamp: {value}");
        }

        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var hmsms = match.Groups["hmsms"].Value;
        var hh = int.Parse(hmsms.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var mm = int.Parse(hmsms.AsSpan(2, 2), CultureInfo.InvariantCulture);
        var ss = int.Parse(hmsms.AsSpan(4, 2), CultureInfo.InvariantCulture);
        var ms = int.Parse(hmsms.AsSpan(6, 3), CultureInfo.InvariantCulture);
        return ((day - 1) * 86400.0) + (hh * 3600.0) + (mm * 60.0) + ss + (ms / 1000.0);
    }

    public static double ParseBusmasterTimestampToSeconds(string value)
    {
        var match = BusmasterTsRegex().Match(value.Trim());
        if (!match.Success)
        {
            throw new FormatException($"Unsupported BUSMASTER timestamp: {value}");
        }

        var hh = int.Parse(match.Groups["hh"].Value, CultureInfo.InvariantCulture);
        var mm = int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture);
        var ss = int.Parse(match.Groups["ss"].Value, CultureInfo.InvariantCulture);
        var fracRaw = match.Groups["frac"].Value;
        var frac = int.Parse(fracRaw, CultureInfo.InvariantCulture) / Math.Pow(10, fracRaw.Length);
        return (hh * 3600.0) + (mm * 60.0) + ss + frac;
    }
}
