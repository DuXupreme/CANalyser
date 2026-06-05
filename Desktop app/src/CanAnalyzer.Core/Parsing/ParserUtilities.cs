using System.Globalization;

namespace CanAnalyzer.Core.Parsing;

internal static class ParserUtilities
{
    private static readonly string[] CommonSkips =
    [
        ";",
        "//",
        "#",
        "date",
        "base",
        "internal",
        "Begin",
        "End",
        ";$FILEVERSION",
        ";$STARTTIME",
        ";$COLUMNS"
    ];

    public static bool IsCommonHeaderOrComment(string line)
    {
        return CommonSkips.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static double ParseDoublePointOrComma(string token)
    {
        return double.Parse(token.Replace(',', '.'), CultureInfo.InvariantCulture);
    }

    public static void ReportFileProgress(
        FileStream stream,
        long length,
        IProgress<Domain.LoadProgress>? progress,
        string labelPrefix,
        int basePercent,
        int percentSpan)
    {
        if (progress is null || length <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(stream.Position / (double)length, 0d, 1d);
        var value = basePercent + (int)Math.Round(ratio * percentSpan);
        progress.Report(new Domain.LoadProgress(labelPrefix, value));
    }
}
