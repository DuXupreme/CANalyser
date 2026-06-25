using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

public sealed class CssSemicolonParser : ICanLogParser
{
    public string Name => "CSS/CL1000";

    public int Probe(string filePath, IReadOnlyList<string> sampleLines)
    {
        var header = sampleLines.Any(IsHeader) ? 70 : 0;
        var rows = sampleLines.Count(static line => line.Count(static c => c == ';') >= 3 && line.Contains('T'));
        return Math.Min(100, header + (rows * 5));
    }

    public async Task<CanLogParseResult?> ParseAsync(
        string filePath, ImportMode mode, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        var rows = new DiskBackedFrameStore();
        var report = new ParseReportBuilder(Name);
        var seenHeader = false;
        long? baseTime = null;
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
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: CSS/CL1000...", 5, 10);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { report.NonData(); continue; }
            if (IsHeader(trimmed)) { seenHeader = true; report.NonData(); continue; }
            if (!seenHeader) { report.NonData(); continue; }

            var parts = trimmed.Split(';');
            if (parts.Length < 4)
            {
                report.Reject(lineNumber, "CSS_COLUMNS", "Minder dan vier CSS-kolommen.", line);
                continue;
            }

            try
            {
                var absolute = TimestampParsers.ParseCssTimestampToNanoseconds(parts[0]);
                baseTime ??= absolute;
                var frameType = string.IsNullOrWhiteSpace(parts[1]) ? 0 : int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                if (frameType is < 0 or > 3) throw new FormatException($"Onbekende CSS frame type code {frameType}.");
                var idToken = parts[2].Trim();
                if (idToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idToken = idToken[2..];
                var id = uint.Parse(idToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var data = HexUtilities.ParseHexPayload(parts[3]);
                var extended = frameType is 1 or 3 || id > 0x7FF;
                var fd = frameType >= 2 || data.Length > 8;
                if (!CanFrameValidation.TryNormalize(id, extended, data.Length, data.Length, fd, out var dlc, out var format, out var error))
                    throw new FormatException(error);
                rows.Append(new RawCanFrame(absolute - baseTime.Value, id, dlc, data,
                    extended ? "Ext" : "Std", string.Empty, extended, rows.Count, lineNumber, format));
                report.Accepted();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                report.Reject(lineNumber, "CSS_VALUE", ex.Message, line);
            }
        }

        if (!seenHeader || rows.Count == 0) { rows.Dispose(); return null; }
        rows.Complete();
        var built = report.Build(mode);
        return new CanLogParseResult(rows, built, built.HasErrors ? DatasetCompleteness.Partial : DatasetCompleteness.Complete);
    }

    private static bool IsHeader(string line)
    {
        var fields = line.Trim().Split(';').Select(static field => field.Trim()).ToArray();
        return fields.Length >= 4 &&
               fields[0].Equals("Timestamp", StringComparison.OrdinalIgnoreCase) &&
               fields[1].Equals("Type", StringComparison.OrdinalIgnoreCase) &&
               fields[2].Equals("ID", StringComparison.OrdinalIgnoreCase) &&
               fields[3].Equals("Data", StringComparison.OrdinalIgnoreCase);
    }
}
