using System.Text.Json.Serialization;

namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Group of signals rendered in one plot panel.
/// </summary>
public sealed class PlotGroup
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("signals")]
    public List<string> Signals { get; set; } = [];

    [JsonPropertyName("offsets")]
    public Dictionary<string, double> Offsets { get; set; } = [];

    [JsonPropertyName("lock_y_axes")]
    public bool LockYAxis { get; set; }

    public static PlotGroup Create(string? title = null, IEnumerable<string>? signals = null, IDictionary<string, double>? offsets = null)
    {
        return new PlotGroup
        {
            Title = title ?? string.Empty,
            Signals = signals?.Distinct().ToList() ?? [],
            Offsets = offsets is null ? [] : new Dictionary<string, double>(offsets),
            LockYAxis = false
        };
    }
}
