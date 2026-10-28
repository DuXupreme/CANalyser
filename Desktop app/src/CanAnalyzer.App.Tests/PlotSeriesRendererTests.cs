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
        var series = (LineSeries)PlotSeriesRenderer.Build(source, new PlotViewOptions { UseDownsampling = false }, new(10.25, 10.75));
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
        var series = (LineSeries)PlotSeriesRenderer.Build(source, new PlotViewOptions { UseDownsampling = false }, new(2.5, 7.5));
        Assert.Equal(5, series.Points.Count);
        Assert.Equal(2, series.Points[0].X);
        Assert.True(double.IsNaN(series.Points[2].X));
        Assert.Equal(8, series.Points[^1].X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GapBreaks_PreserveContainmentForUnsortedOverlappingAndInvalidGaps(bool unordered)
    {
        double[] x = unordered
            ? [10, 7, 7, 3, double.NaN, 5, -2, double.NegativeInfinity, 1, double.PositiveInfinity]
            : [0, 1, 1, 3, 5, 7, 8, 10];
        MeasurementGap[] gaps =
        [
            new(7, 8), new(0, 9), new(3, 7), new(1, 1), new(5, 6), new(-1, 0),
            new(double.NaN, 3), new(0, double.NaN), new(8, 2),
            new(double.NegativeInfinity, -3), new(12, double.PositiveInfinity)
        ];
        var y = Enumerable.Range(0, x.Length).Select(i => (double)i).ToArray();
        var source = new RenderedSeriesData("test", x, y, "Y1", OxyColors.Blue) { Gaps = gaps };
        var series = (LineSeries)PlotSeriesRenderer.Build(source,
            new PlotViewOptions { UseDownsampling = true, MaxPointsPerTrace = 200 });

        // The previous containment contract is intentionally independent of gap ordering,
        // overlap, timestamp direction and non-finite endpoints.
        var expected = new List<DataPoint>();
        for (var i = 0; i < x.Length; i++)
        {
            if (i > 0 && gaps.Any(gap => Math.Min(x[i - 1], x[i]) <= gap.StartSeconds &&
                Math.Max(x[i - 1], x[i]) >= gap.EndSeconds)) expected.Add(DataPoint.Undefined);
            expected.Add(new(x[i], y[i]));
        }
        Assert.Equal(expected.Select(p => p.X), series.Points.Select(p => p.X));
        Assert.Equal(expected.Select(p => p.Y), series.Points.Select(p => p.Y));
    }

    [Fact]
    public void SharedGaps_AreIndexedOnceInsteadOfEnumeratedForEveryPoint()
    {
        var gaps = new CountingGaps(Enumerable.Range(0, 128)
            .Select(i => new MeasurementGap(10_000 + i * 2, 10_001 + i * 2)).ToArray());
        var x = Enumerable.Range(0, 2_000).Select(i => (double)i).ToArray();
        var source = new RenderedSeriesData("first", x, x, "Y1", OxyColors.Blue) { Gaps = gaps };
        var options = new PlotViewOptions { UseDownsampling = true, MaxPointsPerTrace = 2_000 };
        var first = (LineSeries)PlotSeriesRenderer.Build(source, options);
        var second = (LineSeries)PlotSeriesRenderer.Build(source with { Label = "second" }, options);

        Assert.Equal(x, first.Points.Select(p => p.X));
        Assert.Equal(x, second.Points.Select(p => p.X));
        Assert.Equal(1, gaps.EnumerationCount);
    }

    private sealed class CountingGaps(MeasurementGap[] gaps) : IReadOnlyList<MeasurementGap>
    {
        public int EnumerationCount { get; private set; }
        public int Count => gaps.Length;
        public MeasurementGap this[int index] => gaps[index];
        public IEnumerator<MeasurementGap> GetEnumerator()
        {
            EnumerationCount++;
            return ((IEnumerable<MeasurementGap>)gaps).GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledBuild_DoesNotPublishSeriesOrChangeSource(bool markers)
    {
        double[] x = [0, 1, 1, 2];
        double[] y = [10, -4, 7, 20];
        var source = new RenderedSeriesData("test", x, y, "Y1", OxyColors.Blue);
        var options = new PlotViewOptions { MarkersOnly = markers };
        Assert.Throws<OperationCanceledException>(() => PlotSeriesRenderer.Build(source, options,
            cancellationToken: new CancellationToken(true)));

        var rebuilt = PlotSeriesRenderer.Build(source, options, new(0, 2));
        Assert.Equal(x, Points(rebuilt).Select(p => p.X));
        Assert.Equal(y, Points(rebuilt).Select(p => p.Y));
        Assert.Equal(new[] { 10d, -4, 7, 20 }, y);
    }

    [Fact]
    public void CancellationDuringGapIndexing_StopsEarlyAndDoesNotCachePartialIndex()
    {
        using var cancellation = new CancellationTokenSource();
        var gaps = new CancellingGaps(cancellation);
        double[] x = [0, 1, 2];
        var source = new RenderedSeriesData("test", x, x, "Y1", OxyColors.Blue) { Gaps = gaps };
        var options = new PlotViewOptions { UseDownsampling = true };

        Assert.Throws<OperationCanceledException>(() => PlotSeriesRenderer.Build(source, options,
            cancellationToken: cancellation.Token));
        Assert.InRange(gaps.Visited, 8, 4097);

        var rebuilt = PlotSeriesRenderer.Build(source, options);
        Assert.Equal(x, Points(rebuilt).Select(p => p.X));
        Assert.Equal(2, gaps.EnumerationCount);
        Assert.Equal(gaps.Count, gaps.Visited);
    }

    private sealed class CancellingGaps(CancellationTokenSource cancellation) : IReadOnlyList<MeasurementGap>
    {
        public int Visited { get; private set; }
        public int EnumerationCount { get; private set; }
        public int Count => 10_000;
        public MeasurementGap this[int index] => new(10_000 + index * 2, 10_001 + index * 2);
        public IEnumerator<MeasurementGap> GetEnumerator()
        {
            EnumerationCount++; Visited = 0;
            for (var i = 0; i < Count; i++)
            {
                Visited++;
                if (Visited == 8) cancellation.Cancel();
                yield return this[i];
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
