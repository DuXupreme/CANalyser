using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.State;

public sealed partial class AppSettings
{
    public SignalWatchOptions SignalWatch { get; set; } = new();
}

public sealed class SignalWatchOptions
{
    public List<SignalIdentity> Signals { get; set; } = [];
    public bool LatestValues { get; set; }
    public bool OnlyWatched { get; set; }
}
