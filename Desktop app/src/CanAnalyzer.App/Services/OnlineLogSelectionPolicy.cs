using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Services;

public static class OnlineLogSelectionPolicy
{
    /// <summary>Only explicitly marked intermediate files may fill a deliberate hole in the selected sequence.</summary>
    public static OnlineLogSelection[] IncludeMarkedBetweenSelections(IReadOnlyList<OnlineLogSelection> selected,
        IEnumerable<OnlineLogSelection> known)
    {
        var result = selected.ToDictionary(file => file.Key, StringComparer.Ordinal);
        var ranges = selected.Where(file => file.Annotation?.Excluded != true)
            .GroupBy(file => (file.Logger, file.Session))
            .Select(group => new { group.Key, Parts = group.Select(file => Part(file.Name)).Where(part => part.HasValue).Select(part => part!.Value).ToArray() })
            .Where(group => group.Parts.Length > 0)
            .ToDictionary(group => group.Key, group => (Min: group.Parts.Min(), Max: group.Parts.Max()));
        foreach (var file in known)
            if (file.Annotation?.Excluded == true && Part(file.Name) is { } part &&
                ranges.TryGetValue((file.Logger, file.Session), out var range) && part > range.Min && part < range.Max)
                result.TryAdd(file.Key, file);
        return result.Values.ToArray();
    }

    private static int? Part(string name) => CanAnalyzer.Core.Domain.OnlineLogSequencePolicy.TryParsePartNumber(name, out var part) ? part : null;
}
