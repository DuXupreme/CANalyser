using System.Globalization;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

/// <summary>
/// CSS / CL1000 style parser: Timestamp;Type;ID;Data.
/// </summary>
public sealed class CssSemicolonParser : ICanLogParser
{
    public string Name => "CSS/CL1000";

    public async Task<IReadOnlyList<RawCanFrame>?> ParseAsync(
        string filePath,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rows = new List<RawCanFrame>();
        var seenHeader = false;
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
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: CSS/CL1000...", 5, 10);
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.Equals("Timestamp;Type;ID;Data", StringComparison.Ordinal))
            {
                seenHeader = true;
                continue;
            }

            if (!seenHeader)
            {
                continue;
            }

            var parts = trimmed.Split(';');
            if (parts.Length < 4)
            {
                continue;
            }

            try
            {
                var absolute = TimestampParsers.ParseCssTimestampToSeconds(parts[0]);
                baseTime ??= absolute;
                var time = absolute - baseTime.Value;

                var frameTypeValue = string.IsNullOrWhiteSpace(parts[1])
                    ? 0
                    : int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                var frameIdToken = parts[2].Trim();
                if (frameIdToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    frameIdToken = frameIdToken[2..];
                }

                var frameId = uint.Parse(frameIdToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var data = HexUtilities.ParseHexPayload(parts[3]);
                rows.Add(
                    new RawCanFrame(
                        TimeSeconds: time,
                        Id: frameId,
                        Dlc: (byte)data.Length,
                        Data: data,
                        Type: frameTypeValue == 1 ? "Ext" : "Std",
                        Channel: string.Empty,
                        IsExtended: frameTypeValue == 1 || frameId > 0x7FF));
            }
            catch
            {
                // Keep permissive parsing behavior from Python implementation.
            }
        }

        if (!seenHeader || rows.Count == 0)
        {
            return null;
        }

        return rows
            .OrderBy(frame => frame.TimeSeconds)
            .ToList();
    }
}
