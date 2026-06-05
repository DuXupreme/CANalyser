using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

/// <summary>
/// BUSMASTER .log/.txt parser.
/// </summary>
public sealed class BusmasterParser : ICanLogParser
{
    public string Name => "BUSMASTER";

    public async Task<IReadOnlyList<RawCanFrame>?> ParseAsync(
        string filePath,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rows = new List<RawCanFrame>();
        double? baseTime = null;

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
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: BUSMASTER...", 5, 10);
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("***", StringComparison.Ordinal) ||
                trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith('#'))
            {
                continue;
            }

            var match = ParserRegex.BusmasterLine().Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            try
            {
                var absolute = TimestampParsers.ParseBusmasterTimestampToSeconds(match.Groups["ts"].Value);
                baseTime ??= absolute;
                var time = absolute - baseTime.Value;

                var frameId = uint.Parse(match.Groups["id"].Value.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var dlc = byte.Parse(match.Groups["dlc"].Value, CultureInfo.InvariantCulture);
                var data = HexUtilities.ParseDataBytes(match.Groups["data"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (data is null || data.Length != dlc)
                {
                    continue;
                }

                rows.Add(
                    new RawCanFrame(
                        TimeSeconds: time,
                        Id: frameId,
                        Dlc: dlc,
                        Data: data,
                        Type: match.Groups["dir"].Value,
                        Channel: match.Groups["channel"].Value,
                        IsExtended: frameId > 0x7FF));
            }
            catch
            {
                // Keep parsing permissive.
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        return rows.OrderBy(frame => frame.TimeSeconds).ToList();
    }
}
