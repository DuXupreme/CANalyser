using System.Text.Json.Serialization;

namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Serialized plot/layout preset.
/// </summary>
public sealed class PlotPreset
{
    [JsonPropertyName("preset_type")]
    public string PresetType { get; set; } = "can-log-viewer-layout";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("saved_at")]
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("plot_groups")]
    public List<PlotGroup> PlotGroups { get; set; } = [];

    [JsonPropertyName("view")]
    public PlotViewOptions View { get; set; } = new();
}

/// <summary>
/// Plot-level filter/options persisted in a preset.
/// </summary>
public sealed class PlotViewOptions
{
    [JsonPropertyName("frame_id_filter")]
    public string? FrameIdFilter { get; set; }

    [JsonPropertyName("time_start")]
    public double? TimeStart { get; set; }

    [JsonPropertyName("time_end")]
    public double? TimeEnd { get; set; }

    [JsonPropertyName("max_points")]
    public int MaxPointsPerTrace { get; set; } = 4000;

    [JsonPropertyName("use_downsampling")]
    public bool UseDownsampling { get; set; } = true;

    [JsonPropertyName("subplot_height")]
    public int SubplotHeight { get; set; } = 280;

    [JsonPropertyName("dropdown_height")]
    public int SignalListHeight { get; set; } = 420;

    [JsonPropertyName("auto_open_detached_on_apply")]
    public bool AutoOpenDetachedOnApply { get; set; }

    public bool NormalizeSignals { get; set; }

    public bool StepPlot { get; set; }

    public bool MarkersOnly { get; set; }

    public bool ShowLegend { get; set; } = true;

    public bool LinkXAxisAcrossPanels { get; set; } = true;

    [JsonPropertyName("plot_options")]
    public List<string> PlotOptions { get; set; } = [];
}
