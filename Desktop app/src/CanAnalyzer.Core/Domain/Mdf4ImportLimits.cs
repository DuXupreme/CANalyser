namespace CanAnalyzer.Core.Domain;

/// <summary>Shared desktop limits; the API request batch size is independent.</summary>
public static class Mdf4ImportLimits
{
    public const int MaximumFiles = 10_000;
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    public const long MaximumBytes = 4L * 1024 * 1024 * 1024;
}
