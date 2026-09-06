using System.Globalization;

namespace CanAnalyzer.Core.Utilities;

/// <summary>
/// Converts exact relative CAN timestamps to absolute measurement timestamps.
/// </summary>
public static class MeasurementTimestamp
{
    private const long NanosecondsPerTick = 100L;

    public static long ToUnixNanoseconds(DateTimeOffset startTimeUtc, long relativeNanoseconds)
    {
        var startTicksSinceUnixEpoch = startTimeUtc.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;
        return checked((startTicksSinceUnixEpoch * NanosecondsPerTick) + relativeNanoseconds);
    }

    public static string FormatUtc(DateTimeOffset startTimeUtc, long relativeNanoseconds) =>
        Format(ToUnixNanoseconds(startTimeUtc, relativeNanoseconds), localTime: false);

    public static string FormatUtcIso8601(DateTimeOffset startTimeUtc, long relativeNanoseconds) =>
        FormatUtcIso8601(ToUnixNanoseconds(startTimeUtc, relativeNanoseconds));

    public static string FormatLocal(DateTimeOffset startTimeUtc, long relativeNanoseconds) =>
        Format(ToUnixNanoseconds(startTimeUtc, relativeNanoseconds), localTime: true);

    public static bool TrySecondsToNanoseconds(double seconds, out long nanoseconds)
    {
        nanoseconds = 0;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return false;
        }

        var scaled = seconds * TimestampParsers.NanosecondsPerSecond;
        if (scaled < long.MinValue || scaled > long.MaxValue)
        {
            return false;
        }

        nanoseconds = checked((long)Math.Round(scaled, MidpointRounding.AwayFromZero));
        return true;
    }

    private static string Format(long unixNanoseconds, bool localTime)
    {
        SplitUnixNanoseconds(unixNanoseconds, out var seconds, out var nanoseconds);

        var instant = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var displayed = localTime ? instant.ToLocalTime() : instant;
        var dateAndTime = displayed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var suffix = localTime
            ? displayed.ToString(" zzz", CultureInfo.InvariantCulture)
            : " UTC";
        return $"{dateAndTime}.{nanoseconds:D9}{suffix}";
    }

    private static string FormatUtcIso8601(long unixNanoseconds)
    {
        SplitUnixNanoseconds(unixNanoseconds, out var seconds, out var nanoseconds);
        var dateAndTime = DateTimeOffset.FromUnixTimeSeconds(seconds)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return $"{dateAndTime}.{nanoseconds:D9}Z";
    }

    private static void SplitUnixNanoseconds(long unixNanoseconds, out long seconds, out long nanoseconds)
    {
        seconds = Math.DivRem(unixNanoseconds, TimestampParsers.NanosecondsPerSecond, out nanoseconds);
        if (nanoseconds >= 0)
        {
            return;
        }

        seconds--;
        nanoseconds += TimestampParsers.NanosecondsPerSecond;
    }
}
