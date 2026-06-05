using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

/// <summary>
/// PEAK .trc parser (classic and tab-separated variants).
/// </summary>
public sealed class PeakTrcParser : ICanLogParser
{
    public string Name => "PEAK .trc";

    public async Task<IReadOnlyList<RawCanFrame>?> ParseAsync(
        string filePath,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rows = new List<RawCanFrame>();

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var length = stream.Length;
        var lineCounter = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            lineCounter++;
            if (lineCounter % 2000 == 0)
            {
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: PEAK .trc...", 5, 10);
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || ParserUtilities.IsCommonHeaderOrComment(trimmed) || trimmed.StartsWith("@ ", StringComparison.Ordinal))
            {
                continue;
            }

            RawCanFrame? parsed = TryParseTsv(trimmed) ?? TryParseClassic(trimmed);
            if (parsed is not null)
            {
                rows.Add(parsed);
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        return rows.OrderBy(frame => frame.TimeSeconds).ToList();
    }

    private static RawCanFrame? TryParseTsv(string line)
    {
        var match = ParserRegex.PeakTrcTsv().Match(line);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            var time = ParserUtilities.ParseDoublePointOrComma(match.Groups["time_ms"].Value) / 1000d;
            var frameId = uint.Parse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var dlc = byte.Parse(match.Groups["dlc"].Value, CultureInfo.InvariantCulture);
            var data = HexUtilities.ParseDataBytes(match.Groups["data"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (data is null || data.Length != dlc)
            {
                return null;
            }

            var tail = match.Groups["tail"].Value.ToLowerInvariant();
            var type = tail.Contains('t') ? "Tx" : tail.Contains('r') ? "Rx" : string.Empty;
            var channel = match.Groups["channel"].Value.Trim();
            var isExtended = tail.Contains('x') || frameId > 0x7FF;

            return new RawCanFrame(time, frameId, dlc, data, type, channel, isExtended);
        }
        catch
        {
            return null;
        }
    }

    private static RawCanFrame? TryParseClassic(string line)
    {
        var match = ParserRegex.PeakTrcClassic().Match(line);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            var time = ParserUtilities.ParseDoublePointOrComma(match.Groups["time_ms"].Value) / 1000d;
            var frameId = uint.Parse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var dlc = byte.Parse(match.Groups["dlc"].Value, CultureInfo.InvariantCulture);
            var data = HexUtilities.ParseDataBytes(match.Groups["data"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (data is null || data.Length != dlc)
            {
                return null;
            }

            var type = match.Groups["dir"].Value.Trim();
            return new RawCanFrame(time, frameId, dlc, data, type, string.Empty, frameId > 0x7FF);
        }
        catch
        {
            return null;
        }
    }
}
