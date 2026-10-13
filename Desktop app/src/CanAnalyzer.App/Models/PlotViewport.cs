namespace CanAnalyzer.App.Models;

/// <summary>The visible time interval captured before preparing a plot.</summary>
public readonly record struct PlotViewport(double Minimum, double Maximum)
{
    public bool IsValid => double.IsFinite(Minimum) && double.IsFinite(Maximum) && Maximum > Minimum;
}
