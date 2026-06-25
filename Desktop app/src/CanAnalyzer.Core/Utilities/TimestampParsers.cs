using System.Globalization;
using System.Text.RegularExpressions;

namespace CanAnalyzer.Core.Utilities;

/// <summary>Exact timestamp parsing helpers. Source decimals are converted without binary floating-point.</summary>
public static partial class TimestampParsers
{
    public const long NanosecondsPerSecond = 1_000_000_000L;
    public const long NanosecondsPerDay = 86_400L * NanosecondsPerSecond;

    [GeneratedRegex("^(?<day>\\d{2})T(?<hmsms>\\d{9})$", RegexOptions.Compiled)]
    private static partial Regex CssTsRegex();

    [GeneratedRegex("^(?<hh>\\d{1,2}):(?<mm>\\d{1,2}):(?<ss>\\d{1,2}):(?<frac>\\d{1,9})$", RegexOptions.Compiled)]
    private static partial Regex BusmasterTsRegex();

    public static long ParseCssTimestampToNanoseconds(string value)
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
        ValidateClock(hh, mm, ss);
        if (day <= 0) throw new FormatException($"Invalid CSS day: {day}");
        return checked(((day - 1L) * NanosecondsPerDay) +
                       (hh * 3_600L * NanosecondsPerSecond) +
                       (mm * 60L * NanosecondsPerSecond) +
                       (ss * NanosecondsPerSecond) +
                       (ms * 1_000_000L));
    }

    public static long ParseBusmasterTimeOfDayToNanoseconds(string value)
    {
        var match = BusmasterTsRegex().Match(value.Trim());
        if (!match.Success)
        {
            throw new FormatException($"Unsupported BUSMASTER timestamp: {value}");
        }

        var hh = int.Parse(match.Groups["hh"].Value, CultureInfo.InvariantCulture);
        var mm = int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture);
        var ss = int.Parse(match.Groups["ss"].Value, CultureInfo.InvariantCulture);
        ValidateClock(hh, mm, ss);
        var fraction = FractionToNanoseconds(match.Groups["frac"].Value);
        return checked((hh * 3_600L * NanosecondsPerSecond) +
                       (mm * 60L * NanosecondsPerSecond) +
                       (ss * NanosecondsPerSecond) + fraction);
    }

    public static long ParseDecimalSecondsToNanoseconds(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var seconds))
        {
            throw new FormatException($"Unsupported decimal timestamp: {value}");
        }

        var scaled = seconds * NanosecondsPerSecond;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new FormatException($"Timestamp has sub-nanosecond precision and cannot be represented exactly: {value}");
        }

        return checked((long)scaled);
    }

    public static long ParseDecimalMillisecondsToNanoseconds(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var milliseconds))
        {
            throw new FormatException($"Unsupported millisecond timestamp: {value}");
        }

        var scaled = milliseconds * 1_000_000m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new FormatException($"Timestamp has sub-nanosecond precision and cannot be represented exactly: {value}");
        }

        return checked((long)scaled);
    }

    public static double ParseCssTimestampToSeconds(string value) =>
        ParseCssTimestampToNanoseconds(value) / (double)NanosecondsPerSecond;

    public static double ParseBusmasterTimestampToSeconds(string value) =>
        ParseBusmasterTimeOfDayToNanoseconds(value) / (double)NanosecondsPerSecond;

    private static long FractionToNanoseconds(string fraction)
    {
        var digits = fraction.PadRight(9, '0');
        return long.Parse(digits, CultureInfo.InvariantCulture);
    }

    private static void ValidateClock(int hh, int mm, int ss)
    {
        if (hh is < 0 or > 23 || mm is < 0 or > 59 || ss is < 0 or > 59)
        {
            throw new FormatException($"Invalid time of day: {hh:D2}:{mm:D2}:{ss:D2}");
        }
    }
}
