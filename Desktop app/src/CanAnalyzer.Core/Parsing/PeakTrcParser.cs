using System.Globalization;
using System.Text.RegularExpressions;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

public sealed class PeakTrcParser : ICanLogParser
{
    public string Name => "PEAK .trc";

    public int Probe(string filePath, IReadOnlyList<string> sampleLines)
    {
        var matches = sampleLines.Count(line => ParserRegex.PeakTrcClassic().IsMatch(line.Trim()) || ParserRegex.PeakTrcTsv().IsMatch(line.Trim()));
        var signature = sampleLines.Any(line => line.Contains("$FILEVERSION", StringComparison.OrdinalIgnoreCase) || line.Contains("$COLUMNS", StringComparison.OrdinalIgnoreCase)) ? 30 : 0;
        var extension = filePath.EndsWith(".trc", StringComparison.OrdinalIgnoreCase) ? 15 : 0;
        return Math.Min(100, signature + extension + (matches * 10));
    }

    public async Task<CanLogParseResult?> ParseAsync(
        string filePath, ImportMode mode, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        var rows = new DiskBackedFrameStore();
        var report = new ParseReportBuilder(Name);
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
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: PEAK .trc...", 5, 10);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || ParserUtilities.IsCommonHeaderOrComment(trimmed) || trimmed.StartsWith("@ ", StringComparison.Ordinal))
            {
                report.NonData();
                continue;
            }

            var match = ParserRegex.PeakTrcTsv().Match(trimmed);
            var tsv = match.Success;
            if (!tsv) match = ParserRegex.PeakTrcClassic().Match(trimmed);
            if (!match.Success)
            {
                report.Reject(lineNumber, "PEAK_SYNTAX", "Regel voldoet niet aan een ondersteunde PEAK TRC-layout.", line);
                continue;
            }

            try
            {
                rows.Append(ParseMatch(match, tsv, rows.Count, lineNumber));
                report.Accepted();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                report.Reject(lineNumber, "PEAK_VALUE", ex.Message, line);
            }
        }

        if (rows.Count == 0) { rows.Dispose(); return null; }
        rows.Complete();
        var built = report.Build(mode);
        return new CanLogParseResult(rows, built, built.HasErrors ? DatasetCompleteness.Partial : DatasetCompleteness.Complete);
    }

    private static RawCanFrame ParseMatch(Match match, bool tsv, long frameIndex, long lineNumber)
    {
        var timeNs = TimestampParsers.ParseDecimalMillisecondsToNanoseconds(match.Groups["time_ms"].Value);
        var id = uint.Parse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var declared = int.Parse(match.Groups["dlc"].Value, CultureInfo.InvariantCulture);
        var data = HexUtilities.ParseDataBytes(match.Groups["data"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                   ?? throw new FormatException("Payload bevat ongeldige hexbytes.");
        var tail = tsv ? match.Groups["tail"].Value : match.Groups["dir"].Value;
        var fd = tail.Contains("fd", StringComparison.OrdinalIgnoreCase) || data.Length > 8;
        var extended = tail.Contains("x", StringComparison.OrdinalIgnoreCase) || id > 0x7FF;
        if (!CanFrameValidation.TryNormalize(id, extended, declared, data.Length, fd, out var dlc, out var format, out var error))
            throw new FormatException(error);
        var direction = CanFrameValidation.ParseDirection(tail);
        var type = direction == CanFrameDirection.Transmit ? "Tx" : direction == CanFrameDirection.Receive ? "Rx" : string.Empty;
        var channel = tsv ? match.Groups["channel"].Value.Trim() : string.Empty;
        return new RawCanFrame(timeNs, id, dlc, data, type, channel, extended, frameIndex, lineNumber, format, direction);
    }
}
