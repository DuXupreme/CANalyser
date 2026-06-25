using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

public sealed class BusmasterParser : ICanLogParser
{
    public string Name => "BUSMASTER";

    public int Probe(string filePath, IReadOnlyList<string> sampleLines)
    {
        var matches = sampleLines.Count(line => ParserRegex.BusmasterLine().IsMatch(line.Trim()));
        var extensionBonus = filePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ? 10 : 0;
        return Math.Min(90, matches * 15) + extensionBonus;
    }

    public async Task<CanLogParseResult?> ParseAsync(
        string filePath,
        ImportMode mode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rows = new DiskBackedFrameStore();
        var report = new ParseReportBuilder(Name);
        long? baseTime = null;
        long dayOffset = 0;
        long? previousTimeOfDay = null;

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var length = stream.Length;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            var lineNumber = report.NextLine();
            if (lineNumber % 2000 == 0)
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: BUSMASTER...", 5, 10);

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("***", StringComparison.Ordinal) ||
                trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('#'))
            {
                report.NonData();
                continue;
            }

            var match = ParserRegex.BusmasterLine().Match(trimmed);
            if (!match.Success)
            {
                report.Reject(lineNumber, "BUSMASTER_SYNTAX", "Regel voldoet niet aan het BUSMASTER-frameformaat.", line);
                continue;
            }

            try
            {
                var timeOfDay = TimestampParsers.ParseBusmasterTimeOfDayToNanoseconds(match.Groups["ts"].Value);
                if (previousTimeOfDay.HasValue && previousTimeOfDay.Value - timeOfDay > TimestampParsers.NanosecondsPerDay / 2)
                    dayOffset += TimestampParsers.NanosecondsPerDay;
                else if (previousTimeOfDay.HasValue && timeOfDay < previousTimeOfDay.Value)
                    report.Warn(lineNumber, "TIME_BACKWARDS", "Timestamp loopt terug binnen dezelfde dag; bronvolgorde blijft behouden.", line);
                previousTimeOfDay = timeOfDay;
                var absolute = checked(dayOffset + timeOfDay);
                baseTime ??= absolute;

                var id = uint.Parse(match.Groups["id"].Value.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var declared = int.Parse(match.Groups["dlc"].Value, CultureInfo.InvariantCulture);
                var data = HexUtilities.ParseDataBytes(match.Groups["data"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                           ?? throw new FormatException("Payload bevat ongeldige hexbytes.");
                var frameType = match.Groups["frame_type"].Value;
                var isExtended = frameType.Contains("x", StringComparison.OrdinalIgnoreCase) || id > 0x7FF;
                var fdMarker = frameType.Contains("fd", StringComparison.OrdinalIgnoreCase) || data.Length > 8;
                if (!CanFrameValidation.TryNormalize(id, isExtended, declared, data.Length, fdMarker, out var dlc, out var format, out var error))
                    throw new FormatException(error);

                var directionText = match.Groups["dir"].Value;
                rows.Append(new RawCanFrame(
                    absolute - baseTime.Value, id, dlc, data, directionText,
                    match.Groups["channel"].Value, isExtended,
                    rows.Count, lineNumber, format, CanFrameValidation.ParseDirection(directionText)));
                report.Accepted();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                report.Reject(lineNumber, "BUSMASTER_VALUE", ex.Message, line);
            }
        }

        if (rows.Count == 0) { rows.Dispose(); return null; }
        rows.Complete();
        var built = report.Build(mode);
        return new CanLogParseResult(rows, built, built.HasErrors ? DatasetCompleteness.Partial : DatasetCompleteness.Complete);
    }
}
