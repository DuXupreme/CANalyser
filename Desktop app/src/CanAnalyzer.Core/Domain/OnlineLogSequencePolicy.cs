using System.Globalization;

namespace CanAnalyzer.Core.Domain;

public sealed record OnlineLogPartIdentity(string Logger, string Session, string FileName);

public sealed record OnlineLogSequenceValidation(bool IsValid, string Message)
{
    public static OnlineLogSequenceValidation Valid { get; } = new(true, string.Empty);
}

/// <summary>
/// Prevents unrelated CANedge sessions from being presented as one continuous measurement.
/// Multiple files are only safe when they are consecutive numbered parts in one logger session.
/// </summary>
public static class OnlineLogSequencePolicy
{
    public static OnlineLogSequenceValidation Validate(IReadOnlyList<OnlineLogPartIdentity> files)
    {
        if (files.Count == 0)
            return new(false, "Selecteer minimaal één MF4-bestand.");
        if (files.Count == 1) return OnlineLogSequenceValidation.Valid;

        var sessionCount = files
            .Select(static file => (file.Logger.Trim(), file.Session.Trim()))
            .Distinct()
            .Count();
        if (sessionCount != 1 || string.IsNullOrWhiteSpace(files[0].Logger) || string.IsNullOrWhiteSpace(files[0].Session))
        {
            return new(false,
                $"Je hebt bestanden uit {sessionCount:N0} verschillende logger-sessies geselecteerd. " +
                "CANalyser mag alleen opeenvolgende MF4-delen samenvoegen die binnen één sessie door de ingestelde maximale bestandsgrootte zijn ontstaan. " +
                "Kies één waarde in de kolom 'Sessie' en analyseer andere sessies afzonderlijk.");
        }

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
