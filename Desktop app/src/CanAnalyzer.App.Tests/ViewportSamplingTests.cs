using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ViewportSamplingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ZoomPanReset_RestoresExactDetailAndPreservesFullAxisBounds(bool step, bool markers)
    {
        using var data = PlotRegressionTests.Dataset(100_000);
        var panel = Assert.Single(new PlotModelBuilder().Build(data, [new PlotGroup { Signals = ["test"] }],
            new PlotViewOptions { UseDownsampling = true, MaxPointsPerTrace = 200, StepPlot = step, MarkersOnly = markers }));
        ViewportSampling.Attach(panel.PlotModel, panel.SeriesData, true, 200);
        var model = panel.PlotModel; var plot = (IPlotModel)model; var xAxis = model.Axes[0]; var yAxis = model.Axes[1];
        DataPoint[] Points() => model.Series[0] is LineSeries l ? l.Points.ToArray()
            : ((ScatterSeries)model.Series[0]).Points.Select(p => new DataPoint(p.X, p.Y)).ToArray();
        plot.Update(true);
        var min = xAxis.ActualMinimum; var max = xAxis.ActualMaximum;
        var yMin = yAxis.ActualMinimum; var yMax = yAxis.ActualMaximum;
        xAxis.Zoom(10, 10.05); plot.Update(false);
        var visible = Points();
        Assert.Contains(visible, p => p.X == 10.025 && p.Y == data.SignalSeriesByLabel["test"].Value[10025]);
        Assert.All(visible, p => Assert.InRange(p.X, 9.999, 10.051));
        // Even data updates while zoomed must not shrink the reset extent or change Y autoscale.
        plot.Update(true);
        Assert.Equal(yMin, yAxis.ActualMinimum); Assert.Equal(yMax, yAxis.ActualMaximum);
        xAxis.Zoom(70, 70.05); plot.Update(false);
        Assert.Contains(Points(), p => p.X == 70.025);
        xAxis.Zoom(101, 102); plot.Update(false); Assert.Empty(Points());
        model.ResetAllAxes(); plot.Update(true);
        Assert.Equal(min, xAxis.ActualMinimum); Assert.Equal(max, xAxis.ActualMaximum);
        Assert.Equal(0, Points()[0].X); Assert.Equal(99.999, Points()[^1].X, 6);
        Assert.Null(model.GetLastPlotException());
    }

    [Fact]
    public void ResampledLines_PreserveMeasurementGapBreaks()
    {
        var x = Enumerable.Range(0, 1000).Select(i => i / 100d).ToArray();
        var data = new RenderedSeriesData("test", x, x, "Y1", OxyColors.Blue)
        { Gaps = [new MeasurementGap(4, 6)] };
        var model = new PlotModel(); model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Key = "Y1" });
        var line = new LineSeries { Title = "test", YAxisKey = "Y1" };
        line.Points.AddRange(x.Where(v => v <= 4 || v >= 6).Select(v => new DataPoint(v, v)));
        // Use a source with the actual missing interval, as produced by import.
        var outside = x.Where(v => v <= 4 || v >= 6).ToArray();
        data = data with { Time = outside, Value = outside };
        model.Series.Add(line); ViewportSampling.Attach(model, [data], true, 200);
        ((IPlotModel)model).Update(true); model.Axes[0].Zoom(3, 7); ((IPlotModel)model).Update(false);
        Assert.Contains(line.Points, p => double.IsNaN(p.X));
    }

    [Fact]
    public void HiddenSeriesAndLinkedAxes_KeepTheirStateAcrossResampling()
    {
        using var data = PlotRegressionTests.Dataset(100_000);
        var panels = new PlotModelBuilder().Build(data,
            [new PlotGroup { Signals = ["test"] }, new PlotGroup { Signals = ["test"] }],
            new PlotViewOptions { UseDownsampling = true, MaxPointsPerTrace = 200 });
        foreach (var p in panels) ((IPlotModel)p.PlotModel).Update(true);
        panels[1].PlotModel.Series[0].IsVisible = false;
        var sync = new XAxisSyncService(); sync.Configure(true, false); sync.Bind(panels.Select(p => p.PlotModel));
        panels[0].PlotModel.Axes[0].Zoom(20, 20.05);
        foreach (var p in panels) ((IPlotModel)p.PlotModel).Update(false);
        Assert.Equal(20, panels[1].PlotModel.Axes[0].ActualMinimum);
        Assert.False(panels[1].PlotModel.Series[0].IsVisible);
        Assert.Contains(((LineSeries)panels[0].PlotModel.Series[0]).Points, p => p.X == 20.025);
    }
}
