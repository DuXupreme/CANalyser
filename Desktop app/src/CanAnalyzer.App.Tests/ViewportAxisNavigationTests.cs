using System.Windows.Controls;
using CanAnalyzer.App.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Wpf;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ViewportAxisNavigationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task WheelAndPanBurst_MatchesOxyPlotRenderingAfterEveryEvent(bool reversed, bool dataMargins) =>
        PlotRegressionTests.OnDispatcher(() =>
        {
            var deferred = new NavigationView(CreateModel(true, reversed, dataMargins), false);
            var immediate = new NavigationView(CreateModel(false, reversed, dataMargins), true);
            foreach (var delta in new[] { 120, 120, 120, -120, 120 })
            {
                Wheel(deferred, delta);
                Wheel(immediate, delta);
                AssertSameRangesAndTransforms(immediate, deferred);
            }

            // A reset/range restore followed by more input must also use the new scale.
            foreach (var view in new[] { deferred, immediate })
            {
                view.ActualModel.Axes[0].Zoom(10, 80);
                view.ActualModel.Axes[1].Zoom(25, 90);
                view.InvalidatePlot(false);
                var pan = new PanManipulator(view);
                var point = Cursor(view);
                pan.Started(new OxyMouseEventArgs { Position = point });
                pan.Delta(new OxyMouseEventArgs { Position = new ScreenPoint(point.X + 12, point.Y - 8) });
                pan.Delta(new OxyMouseEventArgs { Position = new ScreenPoint(point.X + 30, point.Y + 5) });
                pan.Completed(new OxyMouseEventArgs { Position = new ScreenPoint(point.X + 30, point.Y + 5) });
            }
            AssertSameRangesAndTransforms(immediate, deferred);
            Assert.Equal(1, deferred.RenderCount);
            Assert.True(immediate.RenderCount > 1);
            return Task.CompletedTask;
        });

    [Fact]
    public Task RepeatedWheelBeforeRender_PreservesDataUnderCursor() => PlotRegressionTests.OnDispatcher(() =>
    {
        var view = new NavigationView(CreateModel(true, false, false), false);
        var point = Cursor(view);
        var before = view.ActualModel.Axes.Select(a => a.InverseTransform(a.IsHorizontal() ? point.X : point.Y)).ToArray();
        for (var i = 0; i < 8; i++) Wheel(view, 120);
        for (var i = 0; i < before.Length; i++)
        {
            var axis = view.ActualModel.Axes[i];
            Assert.Equal(before[i], axis.InverseTransform(axis.IsHorizontal() ? point.X : point.Y), 8);
        }
        Assert.Equal(1, view.RenderCount);
        return Task.CompletedTask;
    });

    private static void Wheel(NavigationView view, int delta) => OxyPlot.PlotCommands.ZoomWheel.Execute(view,
        view.ActualController, new OxyMouseWheelEventArgs { Delta = delta, Position = Cursor(view) });

    private static ScreenPoint Cursor(NavigationView view)
    {
        var area = view.ActualModel.PlotArea;
        return new ScreenPoint(area.Left + area.Width * 0.43, area.Top + area.Height * 0.58);
    }

    private static void AssertSameRangesAndTransforms(NavigationView expected, NavigationView actual)
    {
        for (var i = 0; i < expected.ActualModel.Axes.Count; i++)
        {
            var a = expected.ActualModel.Axes[i];
            var b = actual.ActualModel.Axes[i];
            Assert.Equal(a.ActualMinimum, b.ActualMinimum, 8);
            Assert.Equal(a.ActualMaximum, b.ActualMaximum, 8);
            Assert.Equal(a.Scale, b.Scale, 8);
            Assert.Equal(a.Offset, b.Offset, 8);
            Assert.Equal(a.ClipMinimum, b.ClipMinimum, 8);
            Assert.Equal(a.ClipMaximum, b.ClipMaximum, 8);
        }
    }

    private static PlotModel CreateModel(bool deferred, bool reversed, bool dataMargins)
    {
        var model = new PlotModel { PlotMargins = new OxyThickness(40) };
        foreach (var position in new[] { AxisPosition.Bottom, AxisPosition.Left })
        {
            LinearAxis axis = deferred ? new ViewportAxis() : new LinearAxis();
            axis.Position = position;
            axis.Minimum = 0; axis.Maximum = 100;
            axis.StartPosition = reversed ? 0.9 : 0.1;
            axis.EndPosition = reversed ? 0.1 : 0.9;
            axis.MinimumMargin = 3; axis.MaximumMargin = 7;
            axis.MinimumDataMargin = dataMargins ? 11 : 0;
            axis.MaximumDataMargin = dataMargins ? 13 : 0;
            model.Axes.Add(axis);
        }
        return model;
    }

    private sealed class NavigationView : IPlotView
    {
        private readonly bool _renderImmediately;
        private readonly Canvas _canvas = new();
        public NavigationView(PlotModel model, bool renderImmediately)
        {
            ActualModel = model;
            _renderImmediately = renderImmediately;
            Render(true);
        }
        public PlotModel ActualModel { get; }
        Model IView.ActualModel => ActualModel;
        public IController ActualController { get; } = new PlotController();
        public OxyRect ClientArea => new(0, 0, 600, 400);
        public int RenderCount { get; private set; }
        public void InvalidatePlot(bool updateData = true)
        {
            if (_renderImmediately) Render(updateData);
        }
        private void Render(bool updateData)
        {
            ((IPlotModel)ActualModel).Update(updateData);
            _canvas.Children.Clear();
            ((IPlotModel)ActualModel).Render(new CanvasRenderContext(_canvas), ClientArea);
            Assert.Null(ActualModel.GetLastPlotException());
            RenderCount++;
        }
        public void SetCursorType(CursorType cursorType) { }
        public void HideZoomRectangle() { }
        public void ShowZoomRectangle(OxyRect rectangle) { }
        public void HideTracker() { }
        public void ShowTracker(TrackerHitResult trackerHitResult) { }
        public void SetClipboardText(string text) { }
    }
}
