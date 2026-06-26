namespace CanAnalyzer.App.Services;

/// <summary>
/// Buckets numeric telemetry so usage remains useful without exposing exact dataset sizes.
/// </summary>
public static class TelemetryBuckets
{
    public static string Count(long value)
    {
        return value switch
        {
            <= 0 => "0",
            <= 10 => "1-10",
            <= 100 => "11-100",
            <= 1_000 => "101-1k",
            <= 10_000 => "1k-10k",
            <= 100_000 => "10k-100k",
            <= 1_000_000 => "100k-1m",
            <= 10_000_000 => "1m-10m",
            _ => "10m+"
        };
    }

    public static string DurationMilliseconds(long value)
    {
        return value switch
        {
            < 1_000 => "<1s",
            < 5_000 => "1s-5s",
            < 15_000 => "5s-15s",
            < 60_000 => "15s-1m",
            < 300_000 => "1m-5m",
            < 900_000 => "5m-15m",
            _ => "15m+"
        };
    }
}
