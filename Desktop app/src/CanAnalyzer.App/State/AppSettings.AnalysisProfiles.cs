using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.State;

public sealed partial class AppSettings
{
    public List<AnalysisProfile> AnalysisProfiles { get; set; } = [];
}
