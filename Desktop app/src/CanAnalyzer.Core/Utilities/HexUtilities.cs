using System.Globalization;
using System.Text.RegularExpressions;

namespace CanAnalyzer.Core.Utilities;

/// <summary>
/// Shared text-to-hex parsing helpers for log readers and filters.
/// </summary>
public static partial class HexUtilities
{
    [GeneratedRegex("^[0-9A-Fa-f]+$", RegexOptions.Compiled)]
    private static partial Regex HexRegex();

    public static uint ParseIntAuto(string token)
    {
        token = token.Trim();
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        if (token.Any(c => "abcdefABCDEF".Contains(c)))
        {
            return uint.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return uint.Parse(token, CultureInfo.InvariantCulture);
    }

    public static byte[]? ParseDataBytes(IEnumerable<string> tokens)
    {
        var bytes = new List<byte>();
        foreach (var tokenRaw in tokens)
        {
            var token = tokenRaw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                token = token[2..];
            }

            if (token.Length is 0 or > 2 || !HexRegex().IsMatch(token))
            {
                return null;
            }

            bytes.Add(byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        return bytes.ToArray();
    }

    public static byte[] ParseHexPayload(string value)
    {
        var payload = (value ?? string.Empty).Trim();
        if (payload.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            payload = payload[2..];
        }

        if (payload.Length % 2 != 0)
        {
            throw new FormatException($"Payload hex length must be even, got {payload.Length}.");
        }

        if (payload.Length > 0 && !HexRegex().IsMatch(payload))
        {
            throw new FormatException("Payload contains non-hex characters.");
        }

        return payload.Length == 0 ? [] : Convert.FromHexString(payload);
    }

    public static string NormalizeHexContainsToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var text = token.Trim().Replace(" ", string.Empty);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return text.ToUpperInvariant();
    }
}
