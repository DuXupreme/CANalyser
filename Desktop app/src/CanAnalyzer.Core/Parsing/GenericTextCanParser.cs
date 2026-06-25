using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

/// <summary>Explicit-only fallback parser. It is never selected by automatic probing.</summary>
public sealed class GenericTextCanParser : ICanLogParser
{
    public string Name => "Generic fallback";
    public int Probe(string filePath, IReadOnlyList<string> sampleLines) => 0;

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
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: generic fallback...", 5, 10);
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || ParserUtilities.IsCommonHeaderOrComment(trimmed)) { report.NonData(); continue; }
            if (TryParseLine(trimmed, rows.Count, lineNumber, out var frame, out var error))
            {
                rows.Append(frame!);
                report.Accepted();
            }
            else
            {
                report.Reject(lineNumber, "GENERIC_SYNTAX", error, line);
            }
        }

        if (rows.Count == 0) { rows.Dispose(); return null; }
        rows.Complete();
        var built = report.Build(mode);
        return new CanLogParseResult(rows, built, built.HasErrors ? DatasetCompleteness.Partial : DatasetCompleteness.Complete);
    }

    private static bool TryParseLine(string line, long frameIndex, long lineNumber, out RawCanFrame? frame, out string error)
    {
        frame = null;
        error = "Geen eenduidige tijd/ID/DLC/payloadcombinatie gevonden.";
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;
        for (var timeIdx = 0; timeIdx < Math.Min(5, parts.Length); timeIdx++)
        {
            long timeNs;
            try { timeNs = TimestampParsers.ParseDecimalSecondsToNanoseconds(parts[timeIdx]); }
            catch { continue; }
            for (var idIdx = timeIdx + 1; idIdx < Math.Min(parts.Length, timeIdx + 8); idIdx++)
            {
                if (parts[idIdx].Equals("rx", StringComparison.OrdinalIgnoreCase) || parts[idIdx].Equals("tx", StringComparison.OrdinalIgnoreCase)) continue;
                uint id;
                try { id = HexUtilities.ParseIntAuto(parts[idIdx]); }
                catch { continue; }
                for (var dlcIdx = idIdx + 1; dlcIdx < Math.Min(parts.Length, idIdx + 6); dlcIdx++)
                {
                    if (!int.TryParse(parts[dlcIdx], out var declared) || declared is < 0 or > 64) continue;
                    var tokens = parts.Skip(dlcIdx + 1).Take(declared).ToArray();
                    var data = HexUtilities.ParseDataBytes(tokens);
                    if (data is null || data.Length != declared) continue;
                    var extended = id > 0x7FF;
                    if (!CanFrameValidation.TryNormalize(id, extended, declared, data.Length, data.Length > 8, out var dlc, out var format, out error)) continue;
                    var directionText = parts.FirstOrDefault(part => part.Equals("rx", StringComparison.OrdinalIgnoreCase) || part.Equals("tx", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                    frame = new RawCanFrame(timeNs, id, dlc, data, directionText, string.Empty, extended,
                        frameIndex, lineNumber, format, CanFrameValidation.ParseDirection(directionText));
                    return true;
                }
            }
        }
        return false;
    }
}
