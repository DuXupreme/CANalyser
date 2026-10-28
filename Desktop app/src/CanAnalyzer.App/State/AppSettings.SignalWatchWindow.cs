namespace CanAnalyzer.App.State;

public sealed partial class AppSettings
{
    public SignalWatchWindowOptions SignalWatchWindow { get; set; } = new();
}

public sealed class SignalWatchWindowOptions
{
    public bool AlwaysOnTop { get; set; } = true;
    public double Width { get; set; } = 640;
    public double Height { get; set; } = 340;
    public double? Left { get; set; }
    public double? Top { get; set; }
}
