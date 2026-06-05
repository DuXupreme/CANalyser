using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

/// <summary>
/// Linux candump-style text parser.
/// </summary>
public sealed class CandumpParser : ICanLogParser
{
    public string Name => "candump";

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
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: candump...", 5, 10);
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var match = ParserRegex.Candump().Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            try
            {
                var absolute = double.Parse(match.Groups["ts"].Value, CultureInfo.InvariantCulture);
                baseTime ??= absolute;
                var time = absolute - baseTime.Value;
                var frameId = uint.Parse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var data = HexUtilities.ParseHexPayload(match.Groups["data"].Value);
                rows.Add(
                    new RawCanFrame(
                        TimeSeconds: time,
                        Id: frameId,
                        Dlc: (byte)data.Length,
                        Data: data,
                        Type: string.Empty,
                        Channel: string.Empty,
                        IsExtended: frameId > 0x7FF));
            }
            catch
            {
                // Intentionally permissive.
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        return rows.OrderBy(frame => frame.TimeSeconds).ToList();
    }
}
