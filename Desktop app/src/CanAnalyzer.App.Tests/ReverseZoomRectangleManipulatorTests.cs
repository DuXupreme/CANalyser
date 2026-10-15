using System.Windows.Controls;
using CanAnalyzer.App.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Wpf;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ReverseZoomRectangleManipulatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Completed_AppliesOnlyFinalRangesAndOneLightweightInvalidation(bool reverse) =>
        PlotRegressionTests.OnDispatcher(() =>
        {
            var view = CreateView();
            var area = view.ActualModel.PlotArea;
            var first = new ScreenPoint(area.Left + area.Width * 0.2, area.Top + area.Height * 0.2);
            var last = new ScreenPoint(area.Left + area.Width * 0.7, area.Top + area.Height * 0.7);
            var start = reverse ? last : first;
            var end = reverse ? first : last;
            var expected = view.ActualModel.Axes.Select(axis =>
            {
                var a = axis.InverseTransform(axis.IsHorizontal() ? start.X : start.Y);
                var b = axis.InverseTransform(axis.IsHorizontal() ? end.X : end.Y);
                var min = Math.Min(a, b);
                var max = Math.Max(a, b);
                if (!reverse) return (min, max);
                var span = axis.ActualMaximum - axis.ActualMinimum;
                var width = span * Math.Clamp(span / (max - min), 1.02, 50);
                var center = (min + max) / 2;
                return (min: center - width / 2, max: center + width / 2);
            }).ToArray();
            var observed = view.ActualModel.Axes.Select(_ => new List<(double Min, double Max)>()).ToArray();
            for (var i = 0; i < observed.Length; i++)
            {
                var axis = view.ActualModel.Axes[i];
                var changes = observed[i];
                axis.AxisChanged += (_, _) => changes.Add((axis.ActualMinimum, axis.ActualMaximum));
            }

            var manipulator = new ReverseZoomRectangleManipulator(view);
            manipulator.Started(new OxyMouseEventArgs { Position = start });
            manipulator.Delta(new OxyMouseEventArgs { Position = end });
            var completed = new OxyMouseEventArgs { Position = end };
            manipulator.Completed(completed);

            Assert.True(completed.Handled);
            Assert.True(view.RectangleHidden);
            Assert.Equal(CursorType.Default, view.Cursor);
            Assert.False(Assert.Single(view.Invalidations));
            for (var i = 0; i < expected.Length; i++)
            {
                var range = Assert.Single(observed[i]);
                Assert.Equal(expected[i].min, range.Min, 8);
                Assert.Equal(expected[i].max, range.Max, 8);
            }
            return Task.CompletedTask;
        });

    [Fact]
    public Task Completed_WithoutDragOnlyClosesSelectionOverlay() => PlotRegressionTests.OnDispatcher(() =>
    {
        var view = CreateView();
        var point = view.ActualModel.PlotArea.Center;
        var manipulator = new ReverseZoomRectangleManipulator(view);
        manipulator.Started(new OxyMouseEventArgs { Position = point });
        var completed = new OxyMouseEventArgs { Position = point };
        manipulator.Completed(completed);

        Assert.True(completed.Handled);
        Assert.True(view.RectangleHidden);
        Assert.Equal(CursorType.Default, view.Cursor);
        Assert.Empty(view.Invalidations);
        return Task.CompletedTask;
    });

    private static RecordingPlotView CreateView()
    {
        var model = new PlotModel();
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Minimum = 0, Maximum = 100 });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Key = "Y1", Minimum = 0, Maximum = 100 });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Right, Key = "Y2", Minimum = 1000, Maximum = 2000 });
        ((IPlotModel)model).Update(true);
        ((IPlotModel)model).Render(new CanvasRenderContext(new Canvas()), new OxyRect(0, 0, 600, 400));
        return new RecordingPlotView(model);
    }

    private sealed class RecordingPlotView(PlotModel model) : IPlotView
    {
        public PlotModel ActualModel { get; } = model;
        Model IView.ActualModel => ActualModel;
        public IController ActualController { get; } = new PlotController();
        public OxyRect ClientArea => new(0, 0, 600, 400);
        public List<bool> Invalidations { get; } = [];
        public bool RectangleHidden { get; private set; }
        public CursorType Cursor { get; private set; }
        public void InvalidatePlot(bool updateData = true) => Invalidations.Add(updateData);
        public void SetCursorType(CursorType cursorType) => Cursor = cursorType;
        public void HideZoomRectangle() => RectangleHidden = true;
        public void ShowZoomRectangle(OxyRect rectangle) => RectangleHidden = false;
        public void HideTracker() { }
        public void ShowTracker(TrackerHitResult trackerHitResult) { }
        public void SetClipboardText(string text) { }
    }
}
