using CanAnalyzer.App.Views;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.Reflection;
using CanAnalyzer.App.Models;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class SharedTimePlotTests
{
    [Fact]
    public Task NewlySelectedBoundedGapPreviewControlsRelatedAxesAndReset() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var context = new object();
        var first = new TimePlotView { DataContext = context, Model = TimeModel(0, 10, true) };
        var sibling = new TimePlotView { DataContext = context, Model = TimeModel(5.8, 5.9, true) };
        try
        {
            await WaitForBuilds(first, sibling);
            first.Model!.Axes[0].Zoom(5.84, 5.88);

            var detail = TimeModel(4855, 6294, true);
            var points = ((LineSeries)detail.Series[0]).Points;
            points.Clear(); points.AddRange([new(4857, 1), new(6292, 2)]);
            first.Model = detail;
            await WaitForBuilds(first, sibling);
            AssertRange(4855, 6294, first, sibling);

            first.Model.Axes[0].Zoom(5000, 5100);
            AssertRange(5000, 5100, first, sibling);
            ClickReset(sibling);
            AssertRange(4855, 6294, first, sibling);

            // A sibling's later unbounded update must not discard this explicit inspection interval.
            sibling.Model = TimeModel(0, 10);
            await WaitForBuilds(first, sibling);
            AssertRange(4855, 6294, first, sibling);
        }
        finally { first.Model = null; sibling.Model = null; }
    });

    [Fact]
    public Task UnboundedOverviewModelsContinueToUseTheirCombinedRange() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var context = new object();
        var first = new TimePlotView { DataContext = context, Model = TimeModel(0, 10) };
        var second = new TimePlotView { DataContext = context, Model = TimeModel(20, 30) };
        try
        {
            await WaitForBuilds(first, second);
            AssertRange(0, 30, first, second);
            first.Model!.Axes[0].Zoom(5, 8);
            ClickReset(second);
            AssertRange(0, 30, first, second);
        }
        finally { first.Model = null; second.Model = null; }
    });

    [Fact]
    public Task ReducedPreviewCannotClaimCompleteSourceDetailAndNormalEventsKeepTheOption() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var model = TimeModel(0, 10, true);
        PlotSourceMetadata.MarkReducedPreview(model);
        var view = new TimePlotView { Model = model };
        try
        {
            await WaitForBuilds(view);
            var grid = (Grid)view.Content;
            var toggle = Assert.Single(((WrapPanel)grid.Children[0]).Children.OfType<CheckBox>());
            var status = (TextBlock)((StackPanel)grid.Children[2]).Children[2];
            Assert.False(toggle.IsEnabled);
            Assert.Contains("volledige meetreeks is niet beschikbaar", Assert.IsType<string>(toggle.ToolTip));
            Assert.Contains("Bronpreview: vooraf beperkt; geen volledige meetreeks", status.Text);
            toggle.IsChecked = true; // Even a programmatic request cannot enable a false exact-source claim.
            await WaitForBuilds(view);
            Assert.False(toggle.IsChecked);
            Assert.Contains("Bronpreview: vooraf beperkt", status.Text);

            view.Model = TimeModel(0, 10, true);
            await WaitForBuilds(view);
            Assert.True(toggle.IsEnabled);
            Assert.Null(toggle.ToolTip);
            Assert.DoesNotContain("vooraf beperkt", status.Text);
        }
        finally { view.Model = null; }
    });

    private static PlotModel TimeModel(double start, double end, bool bounded = false)
    {
        var model = new PlotModel();
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom,
            Minimum = bounded ? start : double.NaN, Maximum = bounded ? end : double.NaN });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left });
        var line = new LineSeries(); line.Points.AddRange([new(start, 1), new(end, 2)]); model.Series.Add(line);
        return model;
    }
    private static async Task WaitForBuilds(params TimePlotView[] views)
    {
        var build = typeof(TimePlotView).GetField("_build", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var retry = 0; retry < 500 && views.Any(view => build.GetValue(view) is not null); retry++) await Task.Delay(10);
        Assert.All(views, view => Assert.Null(build.GetValue(view)));
        foreach (var view in views) ((IPlotModel)view.Model!).Update(true);
    }
    private static void AssertRange(double start, double end, params TimePlotView[] views)
    {
        foreach (var view in views)
        {
            ((IPlotModel)view.Model!).Update(false);
            Assert.Equal(start, view.Model.Axes[0].ActualMinimum);
            Assert.Equal(end, view.Model.Axes[0].ActualMaximum);
        }
    }
    private static void ClickReset(TimePlotView view)
        => ((WrapPanel)((Grid)view.Content).Children[0]).Children.OfType<Button>()
            .Single(button => Equals(button.Content, "Reset")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void SourceCaptureRetainsGapsAndDoesNotEditOriginalPoints()
    {
        var model = new PlotModel();
        var line = new StairStepSeries { Title = "Position" };
        line.Points.AddRange([new(0, 1), new(1, 2), DataPoint.Undefined, new(2, 3), new(3, 4)]);
        model.Series.Add(line);
        var sources = (IReadOnlyList<RenderedSeriesData>)typeof(TimePlotView)
            .GetMethod("CaptureSources", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [model])!;
        Assert.Equal(new[] { 0d, 1d, 2d, 3d }, Assert.Single(sources).Time);
        Assert.Equal(new CanAnalyzer.Core.Domain.MeasurementGap(1, 2), Assert.Single(sources[0].Gaps));
        Assert.Equal(5, line.Points.Count); Assert.True(double.IsNaN(line.Points[2].X));
    }

    [Fact]
    public Task CursorBubblesExactLogTimeAndReplacementDropsOldCursor() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var model = new PlotModel(); model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left });
        var line = new LineSeries(); line.Points.AddRange([new(0, 1), new(1, 2)]); model.Series.Add(line);
        var view = new TimePlotView { Model = model };
        double? cursor = null; view.CursorMoved += (_, e) => cursor = e.TimeSeconds;
        typeof(TimePlotView).GetMethod("SetCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [.25]);
        Assert.Equal(.25, cursor);
        view.Model = null;
        await Task.Delay(50);
        Assert.Null(view.Model);
    });

    [Fact]
    public Task RelatedTimePlotsShareCursorFlagsAndXAxisButOtherContextsStayIndependent() => PlotRegressionTests.OnDispatcher(async () =>
    {
        static PlotModel Model()
        {
            var model = new PlotModel(); model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left });
            var series = new LineSeries(); series.Points.AddRange([new(0, 1), new(10, 2)]); model.Series.Add(series); return model;
        }
        var context = new object();
        var first = new TimePlotView { DataContext = context, Model = Model() };
        var second = new TimePlotView { DataContext = context, Model = Model() };
        var unrelated = new TimePlotView { DataContext = new object(), Model = Model() };
        var otherGroup = new TimePlotView { DataContext = context, InteractionGroup = "other", Model = Model() };
        var views = new[] { first, second, unrelated, otherGroup };
        var build = typeof(TimePlotView).GetField("_build", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var retry = 0; retry < 500 && views.Any(view => build.GetValue(view) is not null); retry++) await Task.Delay(10);
        Assert.All(views, view => Assert.Null(build.GetValue(view)));
        foreach (var view in views) ((IPlotModel)view.Model!).Update(true);
        typeof(TimePlotView).GetMethod("SetCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(first, [2.5]);
        var cursor = typeof(TimePlotView).GetField("_cursor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(2.5, cursor.GetValue(second)); Assert.Null(cursor.GetValue(unrelated)); Assert.Null(cursor.GetValue(otherGroup));
        var toolbar = (WrapPanel)((Grid)first.Content).Children[0];
        ((Button)toolbar.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var flag = typeof(TimePlotView).GetField("_a", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(2.5, flag.GetValue(second)); Assert.Null(flag.GetValue(unrelated)); Assert.Null(flag.GetValue(otherGroup));
        first.Model!.Axes[0].Zoom(2, 4);
        ((IPlotModel)second.Model!).Update(false);
        Assert.Equal(2, second.Model.Axes[0].ActualMinimum); Assert.Equal(4, second.Model.Axes[0].ActualMaximum);
        Assert.NotEqual(2, unrelated.Model!.Axes[0].ActualMinimum);
        first.Model = Model(); // Selecting another event leaves the previous manual zoom.
        ((IPlotModel)second.Model).Update(false);
        Assert.Equal(0, second.Model.Axes[0].ActualMinimum); Assert.Equal(10, second.Model.Axes[0].ActualMaximum);
        first.Model = new PlotModel(); second.Model = new PlotModel();
        Assert.Null(cursor.GetValue(first)); Assert.Null(flag.GetValue(second)); // Dataset reset uses empty placeholder models.
        foreach (var view in views) view.Model = null;
    });
}
