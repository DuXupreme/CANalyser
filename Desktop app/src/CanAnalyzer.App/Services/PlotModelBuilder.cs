using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Utilities;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using System.Runtime.CompilerServices;

namespace CanAnalyzer.App.Services;

/// <inheritdoc />
public sealed class PlotModelBuilder : IPlotModelBuilder
{
    private const string AxisNumberFormat = "0.###";
    private readonly ConditionalWeakTable<double[], ConditionalWeakTable<double[], SignalRangeIndex>> _indices = new();

    private static readonly OxyColor[] ColorCycle =
    [
        OxyColor.Parse("#1F77B4"),
        OxyColor.Parse("#FF7F0E"),
        OxyColor.Parse("#2CA02C"),
        OxyColor.Parse("#D62728"),
        OxyColor.Parse("#9467BD"),
        OxyColor.Parse("#8C564B"),
        OxyColor.Parse("#E377C2"),
        OxyColor.Parse("#7F7F7F"),
        OxyColor.Parse("#BCBD22"),
        OxyColor.Parse("#17BECF")
    ];

    public IReadOnlyList<PlotPanelModel> Build(
        CanDataset dataset,
        IReadOnlyList<PlotGroup> plotGroups,
        PlotViewOptions viewOptions,
        IReadOnlyList<PlotViewport?>? viewports = null)
    {
        var panels = new List<PlotPanelModel>();
        var controller = CreateInteractionController();
        uint? frameIdFilter = null;
        if (!string.IsNullOrWhiteSpace(viewOptions.FrameIdFilter))
        {
            try
            {
                frameIdFilter = HexUtilities.ParseIntAuto(viewOptions.FrameIdFilter);
            }
            catch
            {
                frameIdFilter = null;
            }
        }

        foreach (var (group, index) in plotGroups.Select((value, idx) => (value, idx)))
        {
            if (group.Signals.Count == 0)
            {
                continue;
            }

            var renderedSeries = new List<RenderedSeriesData>();
            var viewport = viewports is not null && index < viewports.Count ? viewports[index] : null;
            var title = string.IsNullOrWhiteSpace(group.Title) ? $"Plot {index + 1}" : group.Title.Trim();
            var model = new PlotModel
            {
                Title = title,
                IsLegendVisible = viewOptions.ShowLegend
            };

            if (model.Legends.Count == 0)
            {
                model.Legends.Add(new Legend
                {
                    LegendPlacement = LegendPlacement.Inside,
                    LegendPosition = LegendPosition.TopRight,
                    LegendOrientation = LegendOrientation.Vertical,
                    LegendBackground = OxyColor.FromAColor(220, OxyColors.White),
                    LegendBorder = OxyColor.FromAColor(140, OxyColors.Gray),
                    LegendBorderThickness = 1
                });
            }

            var xAxis = new ViewportAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Tijd [s]",
                StringFormat = AxisNumberFormat,
                MinimumPadding = 0.01,
                MaximumPadding = 0.01,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(48, OxyColors.Gray),
                MinorGridlineColor = OxyColor.FromAColor(20, OxyColors.Gray)
            };
            model.Axes.Add(xAxis);
            foreach (var gap in dataset.ImportReport?.Gaps ?? [])
            {
                if (gap.EndSeconds - gap.StartSeconds < 1) continue;
                model.Annotations.Add(new OxyPlot.Annotations.RectangleAnnotation
                {
                    MinimumX = gap.StartSeconds, MaximumX = gap.EndSeconds,
                    Fill = OxyColor.FromAColor(35, OxyColors.Gray),
                    Text = $"Geen data: {gap.EndSeconds - gap.StartSeconds:0.###} s",
                    Layer = OxyPlot.Annotations.AnnotationLayer.BelowSeries
                });
            }

            var primaryYAxis = new ViewportAxis
            {
                Position = AxisPosition.Left,
                Key = "Y1",
                Title = group.Signals[0],
                StringFormat = AxisNumberFormat,
                MinimumPadding = 0.05,
                MaximumPadding = 0.08,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(48, OxyColors.Gray),
                MinorGridlineColor = OxyColor.FromAColor(20, OxyColors.Gray)
            };
            model.Axes.Add(primaryYAxis);

            var colorIndex = 0;
            for (var signalIndex = 0; signalIndex < group.Signals.Count; signalIndex++)
            {
                var label = group.Signals[signalIndex];
                if (frameIdFilter.HasValue && !label.Contains($"[0x{frameIdFilter.Value:X}]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!dataset.SignalSeriesByLabel.TryGetValue(label, out var signalSeries))
                {
                    continue;
                }

                var filtered = FilterAndTransformSeries(signalSeries, group, label, viewOptions);
                var yAxisKey = "Y1";
                if (!group.LockYAxis && signalIndex > 0)
                {
                    yAxisKey = $"Y{signalIndex + 1}";
                    var rightAxis = new ViewportAxis
                    {
                        Position = AxisPosition.Right,
                        PositionTier = signalIndex - 1,
                        Key = yAxisKey,
                        Title = label,
                        MinimumPadding = 0.05,
                        MaximumPadding = 0.08,
                        TextColor = ColorCycle[colorIndex % ColorCycle.Length],
                        AxislineColor = ColorCycle[colorIndex % ColorCycle.Length],
                        StringFormat = AxisNumberFormat
                    };
                    model.Axes.Add(rightAxis);
                }

                if (filtered.X.Length == 0)
                {
                    continue;
                }

                var seriesColor = ColorCycle[colorIndex % ColorCycle.Length];
                // Source buffers are immutable. Reuse the range index when only display options change.
                var values = _indices.GetValue(filtered.X, static _ => new());
                var rangeIndex = values.GetValue(filtered.Y, y => new(filtered.X, y));
                var source = new RenderedSeriesData(label, filtered.X, filtered.Y, yAxisKey, seriesColor)
                { Gaps = dataset.ImportReport?.Gaps ?? [], RangeIndex = rangeIndex };
                renderedSeries.Add(source);
                model.Series.Add(PlotSeriesRenderer.Build(source, viewOptions, viewport));
                colorIndex++;
            }

            if (viewport is { IsValid: true } range)
                xAxis.Zoom(range.Minimum, range.Maximum);
            ViewportSampling.Attach(model, renderedSeries, viewOptions.UseDownsampling, viewOptions.MaxPointsPerTrace, viewport);
            panels.Add(new PlotPanelModel
            {
                Title = title,
                PlotModel = model,
                PlotController = controller,
                SeriesData = renderedSeries
            });
        }

        return panels;
    }

    private static (double[] X, double[] Y) FilterAndTransformSeries(
        SignalSeries source,
        PlotGroup group,
        string label,
        PlotViewOptions options)
    {
        var hasOffset = group.Offsets.TryGetValue(label, out var offset);
        // SignalSeries exposes immutable source buffers. Styling does not require a data copy.
        if (!options.TimeStart.HasValue && !options.TimeEnd.HasValue &&
            (!hasOffset || offset == 0) && !options.NormalizeSignals)
            return (source.Time, source.Value);

        var xOut = new List<double>(source.Time.Length);
        var yOut = new List<double>(source.Value.Length);

        for (var i = 0; i < source.Time.Length; i++)
        {
            var x = source.Time[i];
            if (options.TimeStart.HasValue && x < options.TimeStart.Value)
            {
                continue;
            }

            if (options.TimeEnd.HasValue && x > options.TimeEnd.Value)
            {
                continue;
            }

            var y = source.Value[i];
            if (hasOffset)
            {
                y += offset;
            }

            xOut.Add(x);
            yOut.Add(y);
        }

        if (options.NormalizeSignals && yOut.Count > 0)
        {
            var min = yOut.Min();
            var max = yOut.Max();
            if (Math.Abs(max - min) < 1e-12)
            {
                for (var i = 0; i < yOut.Count; i++)
                {
                    yOut[i] = 0;
                }
            }
            else
            {
                var range = max - min;
                for (var i = 0; i < yOut.Count; i++)
                {
                    yOut[i] = (yOut[i] - min) / range;
                }
            }
        }

        return (xOut.ToArray(), yOut.ToArray());
    }

    private static IPlotController CreateInteractionController()
    {
        var controller = new PlotController();
        controller.UnbindAll();
        var reverseAwareZoomCommand = new DelegatePlotCommand<OxyMouseDownEventArgs>((view, ctrl, args) =>
        {
            ctrl.AddMouseManipulator(view, new ReverseZoomRectangleManipulator(view), args);
        });
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Left, OxyModifierKeys.None, 1), reverseAwareZoomCommand);
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Right, OxyModifierKeys.None, 1), PlotCommands.PanAt);
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Left, OxyModifierKeys.None, 2), PlotCommands.ResetAt);
        controller.Bind(new OxyMouseWheelGesture(OxyModifierKeys.None), PlotCommands.ZoomWheel);
        return controller;
    }
}
