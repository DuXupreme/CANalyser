using System.Runtime.CompilerServices;
using OxyPlot;

namespace CanAnalyzer.App.Models;

/// <summary>Describes display-source limits without taking ownership of a plot model's Tag.</summary>
public static class PlotSourceMetadata
{
    private static readonly ConditionalWeakTable<PlotModel, object> ReducedPreviews = new();

    public static void MarkReducedPreview(PlotModel model) => ReducedPreviews.GetValue(model, _ => new object());
    public static bool IsReducedPreview(PlotModel model) => ReducedPreviews.TryGetValue(model, out _);
}
