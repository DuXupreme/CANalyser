using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Parsing;

/// <summary>
/// Generic fallback parser that infers columns from free-form text lines.
/// </summary>
public sealed class GenericTextCanParser : ICanLogParser
{
    public string Name => "Generic text";

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
                ParserUtilities.ReportFileProgress(stream, length, progress, "Parser: generic fallback...", 5, 10);
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || ParserUtilities.IsCommonHeaderOrComment(trimmed))
            {
                continue;
            }

            var parsed = TryParseLine(trimmed);
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

    private static RawCanFrame? TryParseLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return null;
        }

        int? timeIdx = null;
        double time = 0;
        for (var i = 0; i < Math.Min(5, parts.Length); i++)
        {
            if (double.TryParse(parts[i].Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                time = parsed;
                timeIdx = i;
                break;
            }
        }

        if (!timeIdx.HasValue)
        {
            return null;
        }

        for (var idIdx = timeIdx.Value + 1; idIdx < Math.Min(parts.Length, timeIdx.Value + 8); idIdx++)
        {
            var tokenId = parts[idIdx];
            if (tokenId.Equals("rx", StringComparison.OrdinalIgnoreCase) ||
                tokenId.Equals("tx", StringComparison.OrdinalIgnoreCase) ||
                tokenId.Equals("dt", StringComparison.OrdinalIgnoreCase) ||
                tokenId.Equals("fd", StringComparison.OrdinalIgnoreCase) ||
                tokenId.Equals("ch", StringComparison.OrdinalIgnoreCase) ||
                tokenId.Equals("channel", StringComparison.OrdinalIgnoreCase) ||
                tokenId == "-")
            {
                continue;
            }

            uint frameId;
            try
            {
                frameId = HexUtilities.ParseIntAuto(tokenId);
            }
            catch
            {
                continue;
            }

            for (var dlcIdx = idIdx + 1; dlcIdx < Math.Min(parts.Length, idIdx + 6); dlcIdx++)
            {
                if (!byte.TryParse(parts[dlcIdx], out var dlc) || dlc > 64)
                {
                    continue;
                }

                var dataTokens = parts.Skip(dlcIdx + 1).Take(dlc).ToArray();
                if (dataTokens.Length != dlc)
                {
                    continue;
                }

                var data = HexUtilities.ParseDataBytes(dataTokens);
                if (data is null)
                {
                    continue;
                }

                var lower = parts.Select(token => token.ToLowerInvariant()).ToArray();
                var type = lower.Contains("rx") ? "Rx" : lower.Contains("tx") ? "Tx" : string.Empty;

                return new RawCanFrame(
                    TimeSeconds: time,
                    Id: frameId,
                    Dlc: dlc,
                    Data: data,
                    Type: type,
                    Channel: string.Empty,
                    IsExtended: frameId > 0x7FF);
            }
        }

        return null;
    }
}
