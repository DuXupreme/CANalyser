using OxyPlot;
using OxyPlot.Axes;

namespace CanAnalyzer.App.Services;

/// <summary>
/// Rectangle zoom with reverse-drag behavior:
/// normal drag zooms to the selected box, reverse drag zooms out.
/// All Y-axes in the subplot are transformed together to avoid inter-series shifts.
/// </summary>
public sealed class ReverseZoomRectangleManipulator : ZoomRectangleManipulator
{
    public ReverseZoomRectangleManipulator(IPlotView plotView)
        : base(plotView)
    {
    }

    public override void Completed(OxyMouseEventArgs e)
    {
        var model = PlotView?.ActualModel;
        var xAxis = XAxis ?? model?.Axes.FirstOrDefault(axis =>
            axis.Position == AxisPosition.Bottom || axis.Position == AxisPosition.Top);
        if (model is null || xAxis is null)
        {
            base.Completed(e);
            return;
        }

        var reverseDrag = IsReverseDrag(StartPosition, e.Position);
        var ranges = CaptureTargetRanges(model, xAxis, reverseDrag, StartPosition, e.Position);

        // Let base manipulator finish rectangle lifecycle (clears selection visual).
        base.Completed(e);

        if (ranges.Count == 0)
        {
            return;
        }

        foreach (var (axis, min, max) in ranges)
        {
            axis.Zoom(min, max);
        }

        PlotView?.InvalidatePlot(false);
        e.Handled = true;
    }

    private static bool IsReverseDrag(ScreenPoint start, ScreenPoint end)
    {
        return end.X < start.X || end.Y < start.Y;
    }

    private static List<(Axis Axis, double Min, double Max)> CaptureTargetRanges(
        PlotModel model,
        Axis xAxis,
        bool reverseDrag,
        ScreenPoint start,
        ScreenPoint end)
    {
        var ranges = new List<(Axis Axis, double Min, double Max)>();

        if (TryCreateTargetRange(xAxis, start.X, end.X, reverseDrag, out var xMin, out var xMax))
        {
            ranges.Add((xAxis, xMin, xMax));
        }

        foreach (var axis in model.Axes.Where(axis =>
                     axis.Position == AxisPosition.Left || axis.Position == AxisPosition.Right))
        {
            if (TryCreateTargetRange(axis, start.Y, end.Y, reverseDrag, out var yMin, out var yMax))
            {
                ranges.Add((axis, yMin, yMax));
            }
        }

        return ranges;
    }

    private static bool TryCreateTargetRange(
        Axis axis,
        double startPixel,
        double endPixel,
        bool reverseDrag,
        out double min,
        out double max)
    {
        min = 0;
        max = 0;

        var p1 = axis.InverseTransform(startPixel);
        var p2 = axis.InverseTransform(endPixel);
        var selMin = Math.Min(p1, p2);
        var selMax = Math.Max(p1, p2);
        var selRange = selMax - selMin;
        if (double.IsNaN(selRange) || selRange <= 1e-12)
        {
            return false;
        }

        if (!reverseDrag)
        {
            min = selMin;
            max = selMax;
            return true;
        }

        return TryCreateZoomOutRange(axis, selMin, selMax, out min, out max);
    }

    private static bool TryCreateZoomOutRange(Axis axis, double selMin, double selMax, out double min, out double max)
    {
        min = 0;
        max = 0;

        var currentMin = axis.ActualMinimum;
        var currentMax = axis.ActualMaximum;
        var currentRange = currentMax - currentMin;
        if (double.IsNaN(currentRange) || currentRange <= 0)
        {
            return false;
        }

        var selRange = selMax - selMin;
        if (double.IsNaN(selRange) || selRange <= 0)
        {
            return false;
        }

        var factor = currentRange / selRange;
        factor = Math.Clamp(factor, 1.02, 50.0);
        var center = (selMin + selMax) * 0.5;
        var newRange = currentRange * factor;
        min = center - (newRange * 0.5);
        max = center + (newRange * 0.5);
        return !double.IsNaN(min) && !double.IsNaN(max) && max > min;
    }
}
