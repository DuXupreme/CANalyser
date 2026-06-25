using System.Text.Json;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;

namespace CanAnalyzer.Core.Export;

/// <inheritdoc />
public sealed class PresetSerializer : IPresetSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Serialize(PlotPreset preset)
    {
        return JsonSerializer.Serialize(preset, Options);
    }

    public PlotPreset Deserialize(string json)
    {
        var preset = JsonSerializer.Deserialize<PlotPreset>(json, Options);
        if (preset is null)
        {
            throw new InvalidDataException("Preset JSON is empty or invalid.");
        }

        if (!string.Equals(preset.PresetType, "can-log-viewer-layout", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Onbekend presettype '{preset.PresetType}'.");
        }

        if (preset.Version is < 1 or > 2)
        {
            throw new InvalidDataException($"Presetversie {preset.Version} wordt niet ondersteund.");
        }

        preset.PlotGroups ??= [];
        preset.View ??= new PlotViewOptions();
        return preset;
    }
}
