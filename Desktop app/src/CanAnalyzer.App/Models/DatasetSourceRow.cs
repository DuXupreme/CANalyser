using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Models;

public sealed record DatasetSourceRow(SourceLogFile Source)
{
    public string Machine => OnlineMachineCatalog.ResolveName(Source.Logger);
    public string Logger => Source.Logger ?? "Niet beschikbaar";
    public string Session => Source.Session ?? "Niet beschikbaar";
    public string Name => Source.Name;
    public string SourcePath => Source.SourcePath;
}
