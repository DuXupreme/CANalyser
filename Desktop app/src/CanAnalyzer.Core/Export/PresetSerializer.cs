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

        preset.PlotGroups ??= [];
        preset.View ??= new PlotViewOptions();
        return preset;
    }
}
