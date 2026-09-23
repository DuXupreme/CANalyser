using System.Globalization;

namespace CanAnalyzer.Core.Domain;

public sealed record OnlineLogPartIdentity(string Logger, string Session, string FileName);

public sealed record OnlineLogSequenceValidation(bool IsValid, string Message)
{
    public static OnlineLogSequenceValidation Valid { get; } = new(true, string.Empty);
}

/// <summary>
/// Validates selections from one logger across multiple sessions.
/// Parts must be consecutive within each session; absolute timestamps preserve gaps.
/// </summary>
public static class OnlineLogSequencePolicy
{
    public static OnlineLogSequenceValidation Validate(IReadOnlyList<OnlineLogPartIdentity> files)
    {
        if (files.Count == 0)
            return new(false, "Selecteer minimaal één MF4-bestand.");
        if (files.Count == 1) return OnlineLogSequenceValidation.Valid;

        if (files.Any(static file => string.IsNullOrWhiteSpace(file.Logger) || string.IsNullOrWhiteSpace(file.Session)) ||
            files.Select(static file => file.Logger.Trim()).Distinct(StringComparer.Ordinal).Count() != 1)
            return new(false, "Selecteer herkenbare logger-sessies van dezelfde machine.");
        foreach (var session in files.GroupBy(static file => file.Session.Trim(), StringComparer.Ordinal))
        {
            var validation = ValidateSession(session.ToArray());
            if (!validation.IsValid) return validation;
        }
        return OnlineLogSequenceValidation.Valid;
    }

    private static OnlineLogSequenceValidation ValidateSession(IReadOnlyList<OnlineLogPartIdentity> files)
    {
        var parts = new List<int>(files.Count);
        foreach (var file in files)
        {
            if (!TryParsePartNumber(file.FileName, out var partNumber))
            {
                return new(false,
                    "CANalyser kan aan de bestandsnamen niet betrouwbaar zien dat dit opeenvolgende delen van één meting zijn. " +
                    "Kies één bestand, of selecteer uitsluitend de originele opeenvolgende MF4-delen uit dezelfde sessie.");
            }
            parts.Add(partNumber);
        }

        parts.Sort();
        for (var index = 1; index < parts.Count; index++)
        {
            if (parts[index] == parts[index - 1])
            {
                return new(false,
                    $"De selectie bevat deel {parts[index]:D8} meer dan één keer. " +
                    "Verwijder het dubbele bestand of kies één bestand.");
            }
            if (parts[index] != parts[index - 1] + 1)
            {
                return new(false,
                    $"De geselecteerde delen zijn niet opeenvolgend: tussen {parts[index - 1]:D8} en {parts[index]:D8} ontbreekt minimaal één deel. " +
                    "Selecteer ook de tussenliggende delen, of analyseer één bestand afzonderlijk.");
            }
        }

        return OnlineLogSequenceValidation.Valid;
    }

    internal static bool TryParsePartNumber(string fileName, out int partNumber)
    {
        partNumber = 0;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) return false;

        var digitCount = 0;
        while (digitCount < stem.Length && char.IsAsciiDigit(stem[digitCount])) digitCount++;
        if (digitCount == 0 || digitCount < stem.Length && stem[digitCount] is not '-' and not '_') return false;
        return int.TryParse(stem.AsSpan(0, digitCount), NumberStyles.None, CultureInfo.InvariantCulture, out partNumber);
    }
}
