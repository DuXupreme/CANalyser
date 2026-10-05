using CanAnalyzer.Core.Export;

namespace CanAnalyzer.App.State;

/// <summary>A local export recipe; contains signal identities and formatting, never measurement values.</summary>
public sealed record CsvExportPreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public CsvExportOptions Options { get; init; } = new();
}
