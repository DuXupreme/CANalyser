using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class PlotSeriesRendererTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ExactViewport_PreservesSamplesAndGlobalAutoscaleAcrossUpdates(bool step, bool markers)
    {
        var source = Source();
        var viewport = new PlotViewport(10, 10.004);
        var series = PlotSeriesRenderer.Build(source,
            new PlotViewOptions { UseDownsampling = false, MaxPointsPerTrace = 200, StepPlot = step, MarkersOnly = markers }, viewport);
        var model = Model(series);
        var xAxis = model.Axes[0]; var yAxis = model.Axes[1];
        xAxis.Zoom(viewport.Minimum, viewport.Maximum);
        ViewportSampling.Attach(model, [source], false, 200, viewport);

        Assert.Equal(7, Points(series).Length);
        ((IPlotModel)model).Update(true);
        Assert.Equal(Enumerable.Range(9999, 7).Select(i => i / 1000d), Points(series).Select(p => p.X));
        Assert.True(yAxis.ActualMinimum < source.Value[0]);
        Assert.True(yAxis.ActualMaximum > source.Value[^1]);
        ((IPlotModel)model).Update(true);
        Assert.Equal(7, Points(series).Length);

        model.ResetAllAxes(); ((IPlotModel)model).Update(true);
        Assert.True(Points(series).Length <= 200);
        Assert.Equal(0, Points(series)[0].X);
        Assert.Equal(source.Time[^1], Points(series)[^1].X);
        Assert.True(xAxis.ActualMinimum < 0);
        Assert.True(xAxis.ActualMaximum > source.Time[^1]);
        Assert.Null(model.GetLastPlotException());
    }

    [Fact]
    public void ResumeAndNavigation_RetireRawPointsBeforeAnotherBuild()
    {
        var source = Source(); var viewport = new PlotViewport(10, 11);
        var series = (LineSeries)PlotSeriesRenderer.Build(source,
            new PlotViewOptions { UseDownsampling = false, MaxPointsPerTrace = 200 }, viewport);
        var model = Model(series); model.Axes[0].Zoom(10, 11);
        ViewportSampling.Attach(model, [source], false, 200, viewport);
        ((IPlotModel)model).Update(true);
        Assert.Equal(1003, series.Points.Count);
        Assert.Null(series.Decimator);

        ViewportSampling.ResumeDownsampling(model, 200);
        Assert.True(series.Points.Count <= 200);
        Assert.NotNull(series.Decimator);
        ((IPlotModel)model).Update(false);
        Assert.True(series.Points.Count <= 200);
        model.Axes[0].Zoom(50, 51); ((IPlotModel)model).Update(false);
        Assert.All(series.Points, p => Assert.InRange(p.X, 49.999, 51.001));
        Assert.True(series.Points.Count <= 200);
    }

    [Fact]
    public void AxisChange_ImmediatelyLeavesRawModeWithoutHostRebuild()
    {
        var source = Source(); var viewport = new PlotViewport(10, 11);
        var series = (LineSeries)PlotSeriesRenderer.Build(source,
            new PlotViewOptions { UseDownsampling = false, MaxPointsPerTrace = 200 }, viewport);
        var model = Model(series); model.Axes[0].Zoom(10, 11);
        ViewportSampling.Attach(model, [source], false, 200, viewport);
        ((IPlotModel)model).Update(true);
        Assert.Equal(1003, series.Points.Count);
        model.Axes[0].Zoom(50, 51); ((IPlotModel)model).Update(false);
        Assert.All(series.Points, p => Assert.InRange(p.X, 49.999, 51.001));
        Assert.True(series.Points.Count <= 200);
        Assert.NotNull(series.Decimator);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(2, 1)]
    [InlineData(1, 1)]
    public void InvalidViewport_UsesBoundedOverview(double minimum, double maximum)
    {
        var series = (LineSeries)PlotSeriesRenderer.Build(Source(),
            new PlotViewOptions { UseDownsampling = false, MaxPointsPerTrace = 200 }, new(minimum, maximum));
        Assert.True(series.Points.Count <= 200);
        Assert.NotNull(series.Decimator);
    }

    [Fact]
    public void UnorderedTimestamps_SelectVisibleSegmentsWithoutBinarySearch()
    {
        double[] x = [1000, 1001, 10, 10.5, 11, 2000, 2001, double.NaN, 4000, 4001];
        var source = new RenderedSeriesData("test", x, x, "Y1", OxyColors.Blue);
        var series = (LineSeries)PlotSeriesRenderer.Build(source, new PlotViewOptions(), new(10.25, 10.75));
        Assert.False(source.RangeIndex.SupportsRangeQueries);
        Assert.Equal(new[] { 1001d, 10, 10.5, 11 }, series.Points.Select(p => p.X));
    }

    [Fact]
    public void UnorderedOverview_RemainsBoundedAndKeepsGlobalExtrema()
    {
        var x = Enumerable.Range(0, 10000).Select(i => (double)(i * 37 % 10000)).ToArray();
        var source = new RenderedSeriesData("test", x, x, "Y1", OxyColors.Blue);
        var series = (LineSeries)PlotSeriesRenderer.Build(source,
            new PlotViewOptions { UseDownsampling = true, MaxPointsPerTrace = 200 });
        Assert.True(series.Points.Count <= 200);
        Assert.Equal(0, series.Points.Min(p => p.X));
        Assert.Equal(9999, series.Points.Max(p => p.X));
    }

    [Fact]
    public void RawViewport_PreservesMeasurementGapBreaks()
    {
        double[] x = [0, 1, 2, 3, 7, 8, 9, 10];
        var source = new RenderedSeriesData("test", x, x, "Y1", OxyColors.Blue)
        { Gaps = [new MeasurementGap(3, 7)] };
        var series = (LineSeries)PlotSeriesRenderer.Build(source, new PlotViewOptions(), new(2.5, 7.5));
        Assert.Equal(5, series.Points.Count);
        Assert.Equal(2, series.Points[0].X);
        Assert.True(double.IsNaN(series.Points[2].X));
        Assert.Equal(8, series.Points[^1].X);
    }

    private static RenderedSeriesData Source()
    {
        var x = Enumerable.Range(0, 100_000).Select(i => i / 1000d).ToArray();
        return new("test", x, x, "Y1", OxyColors.Blue);
    }

    private static PlotModel Model(Series series)
    {
        var model = new PlotModel();
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Key = "Y1" });
        model.Series.Add(series);
        return model;
    }

    private static DataPoint[] Points(Series series) => series is LineSeries line
        ? line.Points.ToArray() : ((ScatterSeries)series).Points.Select(p => new DataPoint(p.X, p.Y)).ToArray();
}
