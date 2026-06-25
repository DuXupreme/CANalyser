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

    [JsonPropertyName("signal_identities")]
    public List<PresetSignalReference> SignalIdentities { get; set; } = [];

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

/// <summary>Stable version-2 preset reference; label is informational only.</summary>
public sealed class PresetSignalReference
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("frame_format")]
    public CanFrameFormat FrameFormat { get; set; }

    [JsonPropertyName("extended")]
    public bool IsExtended { get; set; }

    [JsonPropertyName("frame_id")]
    public uint FrameId { get; set; }

    [JsonPropertyName("message")]
    public string MessageName { get; set; } = string.Empty;

    [JsonPropertyName("signal")]
    public string SignalName { get; set; } = string.Empty;

    [JsonPropertyName("display_label")]
    public string DisplayLabel { get; set; } = string.Empty;

    public SignalIdentity ToIdentity() =>
        new(Channel, FrameFormat, IsExtended, FrameId, MessageName, SignalName);

    public static PresetSignalReference From(SignalSeries series) => new()
    {
        Channel = series.Identity.Channel,
        FrameFormat = series.Identity.FrameFormat,
        IsExtended = series.Identity.IsExtended,
        FrameId = series.Identity.FrameId,
        MessageName = series.Identity.MessageName,
        SignalName = series.Identity.SignalName,
        DisplayLabel = series.Label
    };
}
