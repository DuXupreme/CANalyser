using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanAnalyzer.App.Views;
using OxyPlot;
using OxyPlot.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace CanAnalyzer.App.Tests;

public sealed class MarkerRenderingRegressionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1d, true)]
    [InlineData(1.5d, true)]
    [InlineData(2d, true)]
    [InlineData(1d, false)]
    public Task DenseOpaqueMarkers_PreserveFiguresCentersClipAndContour(double dpi, bool stroked)
        => PlotRegressionTests.OnDispatcher(() =>
    {
        var baseline = new Canvas { Width = 240, Height = 100, Background = Brushes.White };
        var cached = new Canvas { Width = 240, Height = 100, Background = Brushes.White };
        var expectedContext = new CanvasRenderContext(baseline) { DpiScale = dpi };
        var actualContext = new CachedCanvasRenderContext(cached) { DpiScale = dpi };
        var rectangles = Enumerable.Range(0, 257).Select(i =>
            new OxyRect(10 + i * 0.7, 35 + Math.Sin(i * 0.31) * 0.3, 5, 5)).ToArray();
        foreach (var context in new CanvasRenderContext[] { expectedContext, actualContext })
        {
            context.PushClip(new OxyRect(17, 0, 160, 100));
            context.DrawEllipses(rectangles, OxyColors.Blue, stroked ? OxyColors.Blue : OxyColors.Undefined,
                1, EdgeRenderingMode.PreferSpeed);
            context.PopClip();
        }
        Assert.Single(baseline.Children.Cast<UIElement>());
        Assert.Equal(17, cached.Children.Count);
        // Compare both WPF geometries: flattening converts source doubles to floats.
        // The original renderer must retain exactly the same figures and positions.
        Assert.Equal(baseline.Children.Cast<System.Windows.Shapes.Path>()
                .SelectMany(path => path.Data.GetFlattenedPathGeometry().Figures).Select(figure => figure.StartPoint),
            cached.Children.Cast<System.Windows.Shapes.Path>()
                .SelectMany(path => path.Data.GetFlattenedPathGeometry().Figures).Select(figure => figure.StartPoint));
        var expected = Pixels(baseline, dpi);
        var actual = Pixels(cached, dpi);
        var width = (int)(240 * dpi);
        var changed = Enumerable.Range(0, expected.Length / 4).Where(i => expected[i * 4 + 2] != actual[i * 4 + 2]).ToArray();
        var maximumBoundaryDistance = changed.Length == 0 ? 0 : changed.Max(i => rectangles.Min(rect =>
            Math.Abs(Math.Sqrt(Math.Pow((i % width + 0.5) / dpi - rect.Center.X, 2) +
                               Math.Pow((i / width + 0.5) / dpi - rect.Center.Y, 2)) - (stroked ? 3 : 2.5)) * dpi));
        // WPF tessellates separate opaque geometries slightly differently at their
        // outer contour. Bound that raster variation; points, centers and clip stay exact.
        Assert.InRange(maximumBoundaryDistance, 0, 1);
        Assert.InRange(changed.Length, 0, (int)Math.Ceiling(actual.Length / 4d * 0.001));
        foreach (var rect in rectangles.Where(rect => rect.Center.X >= 17 && rect.Center.X < 177))
        {
            var center = ((int)(rect.Center.Y * dpi) * width + (int)(rect.Center.X * dpi)) * 4;
            Assert.Equal(0, expected[center + 2]);
            Assert.Equal(0, actual[center + 2]);
        }
        for (var pixel = 0; pixel < actual.Length / 4; pixel++)
        {
            Assert.Equal(255, actual[pixel * 4]);
            Assert.Equal(actual[pixel * 4 + 1], actual[pixel * 4 + 2]);
            Assert.Equal(255, actual[pixel * 4 + 3]);
            var x = pixel % width;
            if (x + 0.5 < 17 * dpi || x + 0.5 >= 177 * dpi)
            {
                Assert.Equal(255, expected[pixel * 4 + 2]);
                Assert.Equal(255, actual[pixel * 4 + 2]);
            }
        }
        output.WriteLine($"dpi={dpi}; changedPixels={changed.Length}; maximumBoundaryDistance={maximumBoundaryDistance:F6} physical pixels; rectangles={rectangles.Length}");
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(128, 255, false, false)]
    [InlineData(255, 128, false, false)]
    [InlineData(255, 255, true, false)]
    [InlineData(255, 255, false, true)]
    public Task AlphaDifferentStrokeAndAntialiasing_KeepOriginalPixels(byte fillAlpha, byte strokeAlpha, bool differentStroke, bool antialias)
        => PlotRegressionTests.OnDispatcher(() =>
    {
        var baseline = new Canvas { Width = 240, Height = 100, Background = Brushes.White };
        var cached = new Canvas { Width = 240, Height = 100, Background = Brushes.White };
        var rectangles = Enumerable.Range(0, 130).Select(i => new OxyRect(i, 35, 5, 5)).ToArray();
        foreach (var context in new CanvasRenderContext[] { new(baseline), new CachedCanvasRenderContext(cached) })
        {
            context.PushClip(new OxyRect(17, 0, 160, 100));
            context.DrawEllipses(rectangles, OxyColor.FromAColor(fillAlpha, OxyColors.Blue),
                OxyColor.FromAColor(strokeAlpha, differentStroke ? OxyColors.Red : OxyColors.Blue), 1,
                antialias ? EdgeRenderingMode.PreferGeometricAccuracy : EdgeRenderingMode.PreferSpeed);
            context.PopClip();
        }
        Assert.Single(cached.Children.Cast<UIElement>());
        Assert.Equal(Pixels(baseline, 1.5), Pixels(cached, 1.5));
        return Task.CompletedTask;
    });

    private static byte[] Pixels(Canvas canvas, double dpi)
    {
        canvas.Measure(new Size(240, 100));
        canvas.Arrange(new Rect(0, 0, 240, 100));
        canvas.UpdateLayout();
        var width = (int)(240 * dpi); var height = (int)(100 * dpi);
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

}
