using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;

namespace CanAnalyzer.Core.Analysis;

/// <inheritdoc />
public sealed class RawFrameFilterService : IRawFrameFilterService
{
    public IReadOnlyList<RawCanFrame> Apply(IReadOnlyList<RawCanFrame> frames, RawFrameFilterOptions options)
    {
        IEnumerable<RawCanFrame> query = frames;

        if (!string.IsNullOrWhiteSpace(options.IdFilter))
        {
            var token = options.IdFilter!.Trim();
            if (TryParseId(token, out var parsedId))
            {
                query = query.Where(frame => frame.Id == parsedId);
            }
            else
            {
                var contains = token.ToUpperInvariant();
                query = query.Where(frame => frame.IdHex.Contains(contains, StringComparison.OrdinalIgnoreCase));
            }
        }

        var payloadToken = HexUtilities.NormalizeHexContainsToken(options.DataContainsHex);
        if (!string.IsNullOrEmpty(payloadToken))
        {
            query = query.Where(frame =>
                frame.DataHex.Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Contains(payloadToken, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(options.TypeContains))
        {
            query = query.Where(frame =>
                frame.Type.Contains(options.TypeContains, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(options.ChannelContains))
        {
            query = query.Where(frame =>
                frame.Channel.Contains(options.ChannelContains, StringComparison.OrdinalIgnoreCase));
        }

        if (options.TimeStart.HasValue)
        {
            var min = options.TimeStart.Value;
            query = query.Where(frame => frame.TimeSeconds >= min);
        }

        if (options.TimeEnd.HasValue)
        {
            var max = options.TimeEnd.Value;
            query = query.Where(frame => frame.TimeSeconds <= max);
        }

        if (options.IsExtended.HasValue)
        {
            var expected = options.IsExtended.Value;
            query = query.Where(frame => frame.IsExtended == expected);
        }

        var maxRows = Math.Max(1, options.MaxRows);
        return query.Skip(Math.Max(0, options.Offset)).Take(maxRows).ToList();
    }

    private static bool TryParseId(string token, out uint frameId)
    {
        try
        {
            frameId = HexUtilities.ParseIntAuto(token);
            return true;
        }
        catch
        {
            frameId = 0;
            return false;
        }
    }
}
