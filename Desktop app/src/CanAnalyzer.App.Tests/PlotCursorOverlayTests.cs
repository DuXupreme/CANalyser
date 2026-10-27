using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CanAnalyzer.App.Views;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class PlotCursorOverlayTests
{
    [Fact]
    public Task CursorBurst_DoesNotUpdateGraphOrChangeSignalAndGapData() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var model = Model();
        var gap = new RectangleAnnotation { MinimumX = 3, MaximumX = 4, Fill = OxyColors.Gray };
        model.Annotations.Add(gap);
        var line = (LineSeries)model.Series[0];
        var originalPoints = line.Points.ToArray();
        var (host, overlay) = Host(model);
        try
        {
            host.Show(); await Settle();
            var updates = 0;
#pragma warning disable CS0618
            model.Updated += (_, _) => updates++;
#pragma warning restore CS0618
            for (var i = 0; i < 40; i++)
                PlotCursorAnnotations.Set(model, [Marker(2 + i / 10d)]);
            await Settle();
            Assert.Equal(0, updates);
            Assert.Same(gap, Assert.Single(model.Annotations));
            Assert.Equal(originalPoints, line.Points);
            Assert.NotEmpty(overlay.Children.Cast<UIElement>());
            Assert.False(overlay.IsHitTestVisible);
            Assert.InRange(RedColumn(overlay, model), model.Axes[0].Transform(5.9) - 2, model.Axes[0].Transform(5.9) + 2);
            Assert.Null(model.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task AxisZoom_RepositionsOverlayWithoutAdditionalGraphUpdate() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var model = Model();
        var (host, overlay) = Host(model);
        try
        {
            host.Show(); await Settle();
            PlotCursorAnnotations.Set(model, [Marker(6)]);
            await Settle();
            var before = RedColumn(overlay, model);
            var updates = 0;
#pragma warning disable CS0618
            model.Updated += (_, _) => updates++;
#pragma warning restore CS0618
            model.Axes[0].Zoom(5, 9);
            model.InvalidatePlot(false);
            await Settle();
            Assert.Equal(1, updates);
            var after = RedColumn(overlay, model);
            Assert.InRange(after, model.Axes[0].Transform(6) - 2, model.Axes[0].Transform(6) + 2);
            Assert.True(Math.Abs(before - after) > 10);
            Assert.Empty(model.Annotations);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task SecondaryAxisAndModelReplacement_UseCurrentTransformsAndReleaseOldState() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var model = Model();
        model.Axes.Add(new LinearAxis { Key = "secondary", Position = AxisPosition.Right, Minimum = 0, Maximum = 200 });
        var (host, overlay) = Host(model);
        try
        {
            host.Show(); await Settle();
            PlotCursorAnnotations.Set(model, [new PointAnnotation
            {
                X = 6, Y = 150, YAxisKey = "secondary", Fill = OxyColors.Red,
                Stroke = OxyColors.Red, Shape = MarkerType.Circle, Size = 4
            }]);
            await Settle();
            var bitmap = Pixels(overlay);
            var x = (int)Math.Round(model.Axes[0].Transform(6));
            var y = (int)Math.Round(model.Axes.Single(axis => axis.Key == "secondary").Transform(150));
            Assert.True(IsRed(bitmap.Bytes, bitmap.Stride, x, y), "The cursor point did not use its own Y axis.");

            var replacement = Model();
            ((CachedPlotView)((Grid)host.Content).Children[0]).Model = replacement;
            overlay.Model = replacement;
            await Settle();
            Assert.Empty(overlay.Children.Cast<UIElement>());
            PlotCursorAnnotations.Set(model, [Marker(2)]);
            await Settle();
            Assert.Empty(overlay.Children.Cast<UIElement>());
        }
        finally { host.Close(); }
    });

    [Fact]
    public void ExportScope_RestoresGapAnnotationsAfterFailure()
    {
        var model = Model();
        var gap = new RectangleAnnotation { MinimumX = 3, MaximumX = 4 };
        model.Annotations.Add(gap);
        var cursor = Marker(6);
        PlotCursorAnnotations.Set(model, [cursor]);
        Assert.Same(gap, Assert.Single(model.Annotations));
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = PlotCursorAnnotations.IncludeForExport(model);
            Assert.Contains(cursor, model.Annotations);
            Assert.Contains(gap, model.Annotations);
            throw new InvalidOperationException("Simulated export failure");
        }));
        Assert.Same(gap, Assert.Single(model.Annotations));
    }

    [Fact]
    public Task OrdinaryPlotView_PreservesAnnotationFallback() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var model = Model();
        var view = new OxyPlot.Wpf.PlotView { Model = model, Width = 480, Height = 250 };
        var host = new Window { Content = view, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); await Settle();
            var first = Marker(2);
            PlotCursorAnnotations.Set(model, [first]);
            Assert.Contains(first, model.Annotations);
            var second = Marker(6);
            PlotCursorAnnotations.Set(model, [second]);
            Assert.DoesNotContain(first, model.Annotations);
            Assert.Same(second, Assert.Single(model.Annotations));
            Assert.Null(model.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task OrdinaryPlotView_SetBeforeAttachTracksDetachReattachAndReplacement() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var model = Model();
        var gap = new RectangleAnnotation { MinimumX = 3, MaximumX = 4 };
        model.Annotations.Add(gap);
        var cursor = Marker(6);
        PlotCursorAnnotations.Set(model, [cursor]);
        Assert.Same(gap, Assert.Single(model.Annotations));
        var view = new OxyPlot.Wpf.PlotView { Width = 480, Height = 250 };
        var host = new Window { Content = view, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); await Settle();
            view.Model = model;
            await Settle();
            Assert.Equal(new Annotation[] { gap, cursor }, model.Annotations);
            model.InvalidatePlot(false);
            model.InvalidatePlot(false);
            Assert.Equal(new Annotation[] { gap, cursor }, model.Annotations);

            // An active export owns the annotations even if the WPF view detaches.
            using (PlotCursorAnnotations.IncludeForExport(model))
            {
                view.Model = null;
                await Settle();
                Assert.Null(model.PlotView);
                Assert.Equal(new Annotation[] { gap, cursor }, model.Annotations);
            }
            Assert.Same(gap, Assert.Single(model.Annotations));
            using (PlotCursorAnnotations.IncludeForExport(model))
                Assert.Equal(new Annotation[] { gap, cursor }, model.Annotations);
            Assert.Same(gap, Assert.Single(model.Annotations));

            view.Model = model;
            await Settle();
            Assert.Equal(new Annotation[] { gap, cursor }, model.Annotations);
            var replacement = Model();
            var replacementCursor = Marker(8);
            PlotCursorAnnotations.Set(replacement, [replacementCursor]);
            view.Model = replacement;
            await Settle();
            Assert.Same(gap, Assert.Single(model.Annotations));
            Assert.Same(replacementCursor, Assert.Single(replacement.Annotations));
            PlotCursorAnnotations.Set(model, [Marker(2)]);
            Assert.Same(replacementCursor, Assert.Single(replacement.Annotations));
            Assert.Null(replacement.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task ViewChanges_TransferCursorBetweenOrdinaryFallbackAndCachedOverlay() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var model = Model();
        var gap = new RectangleAnnotation { MinimumX = 3, MaximumX = 4 };
        model.Annotations.Add(gap);
        var ordinary = new OxyPlot.Wpf.PlotView { Model = model };
        var overlay = new PlotCursorOverlay { Model = model };
        var grid = new Grid { Width = 480, Height = 250 };
        grid.Children.Add(ordinary); grid.Children.Add(overlay);
        var host = new Window { Content = grid, Width = 510, Height = 290,
            Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show(); await Settle();
            var first = Marker(2);
            PlotCursorAnnotations.Set(model, [first]);
            await Settle();
            Assert.Equal(new Annotation[] { gap, first }, model.Annotations);
            Assert.Empty(overlay.Children.Cast<UIElement>());

            ordinary.Model = null;
            grid.Children.Remove(ordinary);
            var cached = new CachedPlotView { Model = model };
            grid.Children.Insert(0, cached);
            await Settle();
            Assert.Same(gap, Assert.Single(model.Annotations));
            Assert.NotEmpty(overlay.Children.Cast<UIElement>());
            Assert.InRange(RedColumn(overlay, model), model.Axes[0].Transform(2) - 2, model.Axes[0].Transform(2) + 2);

            var updates = 0;
#pragma warning disable CS0618
            model.Updated += (_, _) => updates++;
#pragma warning restore CS0618
            var second = Marker(6);
            PlotCursorAnnotations.Set(model, [second]);
            await Settle();
            Assert.Equal(0, updates);
            Assert.Same(gap, Assert.Single(model.Annotations));
            Assert.InRange(RedColumn(overlay, model), model.Axes[0].Transform(6) - 2, model.Axes[0].Transform(6) + 2);

            cached.Model = null;
            grid.Children.Remove(cached);
            ordinary.Model = model;
            grid.Children.Insert(0, ordinary);
            await Settle();
            Assert.Equal(new Annotation[] { gap, second }, model.Annotations);
            Assert.Empty(overlay.Children.Cast<UIElement>());
            Assert.Null(model.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public void NestedExportScopes_KeepLatestStateUntilLastScopeAndRestoreAfterFailure()
    {
        var model = Model();
        var gap = new RectangleAnnotation { MinimumX = 3, MaximumX = 4 };
        model.Annotations.Add(gap);
        var first = Marker(2);
        var second = Marker(6);
        PlotCursorAnnotations.Set(model, [first]);
        var outer = PlotCursorAnnotations.IncludeForExport(model);
        var inner = PlotCursorAnnotations.IncludeForExport(model);
        try
        {
            PlotCursorAnnotations.Set(model, [second, second]);
            Assert.Equal(new Annotation[] { gap, second }, model.Annotations);
            outer.Dispose();
            Assert.Equal(new Annotation[] { gap, second }, model.Annotations);
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var nested = PlotCursorAnnotations.IncludeForExport(model);
                throw new InvalidOperationException("Simulated nested export failure");
            }));
            Assert.Equal(new Annotation[] { gap, second }, model.Annotations);
        }
        finally { inner.Dispose(); outer.Dispose(); }
        Assert.Same(gap, Assert.Single(model.Annotations));
        using (PlotCursorAnnotations.IncludeForExport(model))
            Assert.Equal(new Annotation[] { gap, second }, model.Annotations);
        Assert.Same(gap, Assert.Single(model.Annotations));
    }

    private static LineAnnotation Marker(double x) => new()
    {
        X = x, Type = LineAnnotationType.Vertical, Color = OxyColors.OrangeRed,
        LineStyle = LineStyle.Solid, StrokeThickness = 2, Text = "A"
    };

    private static PlotModel Model()
    {
        var model = new PlotModel();
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Minimum = 0, Maximum = 10 });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Maximum = 10 });
        model.Series.Add(new LineSeries { Points = { new(0, 0), new(10, 10) } });
        return model;
    }

    private static (Window Host, PlotCursorOverlay Overlay) Host(PlotModel model)
    {
        var grid = new Grid { Width = 480, Height = 250 };
        grid.Children.Add(new CachedPlotView { Model = model });
        var overlay = new PlotCursorOverlay { Model = model };
        grid.Children.Add(overlay);
        return (new Window
        {
            Content = grid, Width = 510, Height = 290, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false
        }, overlay);
    }

    private static (byte[] Bytes, int Stride) Pixels(PlotCursorOverlay overlay)
    {
        var width = (int)overlay.ActualWidth;
        var height = (int)overlay.ActualHeight;
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(overlay);
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        return (pixels, width * 4);
    }

    private static double RedColumn(PlotCursorOverlay overlay, PlotModel model)
    {
        var pixels = Pixels(overlay);
        var y = (int)(model.PlotArea.Top + model.PlotArea.Height / 2);
        var columns = Enumerable.Range(0, (int)overlay.ActualWidth)
            .Where(x => IsRed(pixels.Bytes, pixels.Stride, x, y)).ToArray();
        Assert.NotEmpty(columns);
        return columns.Average();
    }

    private static bool IsRed(byte[] pixels, int stride, int x, int y)
    {
        var i = y * stride + x * 4;
        return i >= 0 && i + 3 < pixels.Length && pixels[i + 2] > 150 && pixels[i + 1] < 110 && pixels[i] < 110;
    }

    private static async Task Settle()
    {
        await Task.Delay(100);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
