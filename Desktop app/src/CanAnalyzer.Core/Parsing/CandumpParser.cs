using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

public sealed class CandumpParser : ICanLogParser
{
    public string Name => "candump";

    public int Probe(string filePath, IReadOnlyList<string> sampleLines) =>
        Math.Min(100, sampleLines.Count(line => ParserRegex.Candump().IsMatch(line.Trim())) * 20);

    public async Task<CanLogParseResult?> ParseAsync(
        string filePath, ImportMode mode, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        var rows = new DiskBackedFrameStore();
        var report = new ParseReportBuilder(Name);
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
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: candump...", 5, 10);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) { report.NonData(); continue; }
            var match = ParserRegex.Candump().Match(trimmed);
            if (!match.Success) { report.Reject(lineNumber, "CANDUMP_SYNTAX", "Regel voldoet niet aan candump-formaat.", line); continue; }

            try
            {
                var absolute = TimestampParsers.ParseDecimalSecondsToNanoseconds(match.Groups["ts"].Value);
                baseTime ??= absolute;
                var id = uint.Parse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var fd = match.Groups["sep"].Value == "##";
                var payloadText = match.Groups["data"].Value;
                var brs = false;
                var esi = false;
                if (fd)
                {
                    if (payloadText.Length == 0) throw new FormatException("CAN FD flags ontbreken na ##.");
                    var flags = byte.Parse(payloadText[..1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    brs = (flags & 0x1) != 0;
                    esi = (flags & 0x2) != 0;
                    payloadText = payloadText[1..];
                }
                var data = HexUtilities.ParseHexPayload(payloadText);
                var extended = id > 0x7FF;
                if (!CanFrameValidation.TryNormalize(id, extended, data.Length, data.Length, fd, out var dlc, out var format, out var error))
                    throw new FormatException(error);
                rows.Append(new RawCanFrame(absolute - baseTime.Value, id, dlc, data, string.Empty,
                    match.Groups["channel"].Value, extended, rows.Count, lineNumber, format,
                    CanFrameDirection.Unknown, CanFrameKind.Data, brs, esi));
                report.Accepted();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                report.Reject(lineNumber, "CANDUMP_VALUE", ex.Message, line);
            }
        }

        if (rows.Count == 0) { rows.Dispose(); return null; }
        rows.Complete();
        var built = report.Build(mode);
        return new CanLogParseResult(rows, built, built.HasErrors ? DatasetCompleteness.Partial : DatasetCompleteness.Complete);
    }
}
