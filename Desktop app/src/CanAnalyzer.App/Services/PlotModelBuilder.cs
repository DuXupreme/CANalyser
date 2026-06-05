using CanAnalyzer.App.Models;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Utilities;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace CanAnalyzer.App.Services;

/// <inheritdoc />
public sealed class PlotModelBuilder : IPlotModelBuilder
{
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
        PlotViewOptions viewOptions)
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

            var xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Tijd [s]",
                StringFormat = "0.###",
                MinimumPadding = 0.01,
                MaximumPadding = 0.01,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromAColor(48, OxyColors.Gray),
                MinorGridlineColor = OxyColor.FromAColor(20, OxyColors.Gray)
            };
            model.Axes.Add(xAxis);

            var primaryYAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Key = "Y1",
                Title = group.Signals[0],
                StringFormat = "0.###",
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
                    var rightAxis = new LinearAxis
                    {
                        Position = AxisPosition.Right,
                        PositionTier = signalIndex - 1,
                        Key = yAxisKey,
                        Title = label,
                        MinimumPadding = 0.05,
                        MaximumPadding = 0.08,
                        TextColor = ColorCycle[colorIndex % ColorCycle.Length],
                        AxislineColor = ColorCycle[colorIndex % ColorCycle.Length]
                    };
                    model.Axes.Add(rightAxis);
                }

                if (filtered.X.Length == 0)
                {
                    continue;
                }

                var seriesColor = ColorCycle[colorIndex % ColorCycle.Length];
                renderedSeries.Add(new RenderedSeriesData(label, filtered.X, filtered.Y, yAxisKey, seriesColor));

                (float[] x, float[] y) = viewOptions.UseDownsampling
                    ? Downsampling.MinMax(filtered.X, filtered.Y, Math.Clamp(viewOptions.MaxPointsPerTrace, 200, 20_000))
                    : (filtered.X, filtered.Y);

                AddSeries(model, x, y, label, yAxisKey, seriesColor, viewOptions);
                colorIndex++;
            }

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

    private static (float[] X, float[] Y) FilterAndTransformSeries(
        SignalSeries source,
        PlotGroup group,
        string label,
        PlotViewOptions options)
    {
        var xOut = new List<float>(source.Time.Length);
        var yOut = new List<float>(source.Value.Length);

        var hasOffset = group.Offsets.TryGetValue(label, out var offset);

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
                y += (float)offset;
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

    private static void AddSeries(
        PlotModel model,
        float[] x,
        float[] y,
        string label,
        string yAxisKey,
        OxyColor color,
        PlotViewOptions options)
    {
        if (options.MarkersOnly)
        {
            var scatter = new ScatterSeries
            {
                Title = label,
                MarkerFill = color,
                MarkerStroke = color,
                MarkerType = MarkerType.Circle,
                MarkerSize = 2.5,
                YAxisKey = yAxisKey,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed
            };

            for (var i = 0; i < x.Length; i++)
            {
                scatter.Points.Add(new ScatterPoint(x[i], y[i]));
            }

            model.Series.Add(scatter);
            return;
        }

        if (options.StepPlot)
        {
            var stair = new StairStepSeries
            {
                Title = label,
                Color = color,
                StrokeThickness = 1.2,
                YAxisKey = yAxisKey,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                Decimator = Decimator.Decimate
            };
            for (var i = 0; i < x.Length; i++)
            {
                stair.Points.Add(new DataPoint(x[i], y[i]));
            }

            model.Series.Add(stair);
            return;
        }

        var line = new LineSeries
        {
            Title = label,
            Color = color,
            StrokeThickness = 1.2,
            YAxisKey = yAxisKey,
            EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            Decimator = Decimator.Decimate
        };
        for (var i = 0; i < x.Length; i++)
        {
            line.Points.Add(new DataPoint(x[i], y[i]));
        }

        model.Series.Add(line);
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
