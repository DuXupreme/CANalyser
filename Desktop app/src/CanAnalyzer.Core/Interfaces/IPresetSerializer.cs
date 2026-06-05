using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Reads/writes plot preset files.
/// </summary>
public interface IPresetSerializer
{
    string Serialize(PlotPreset preset);

    PlotPreset Deserialize(string json);
}
