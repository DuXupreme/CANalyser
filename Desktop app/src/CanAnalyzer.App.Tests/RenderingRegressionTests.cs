using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanAnalyzer.App.Views;
using OxyPlot;
using OxyPlot.Wpf;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class RenderingRegressionTests
{
    [Fact]
    public Task CachedPlotView_UsesNormalTemplateControllerAndAxes() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var dataset = PlotRegressionTests.Dataset();
        var panel = Assert.Single(new CanAnalyzer.App.Services.PlotModelBuilder().Build(dataset,
            [new CanAnalyzer.Core.Domain.PlotGroup { Signals = ["test"] }], new CanAnalyzer.Core.Domain.PlotViewOptions()));
        var view = new CachedPlotView { Width = 500, Height = 250, Model = panel.PlotModel, Controller = panel.PlotController };
        var host = new Window { Content = view, Width = 520, Height = 290, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Same(panel.PlotController, view.Controller);
            Assert.True(panel.PlotModel.PlotArea.Width > 0, "The hosted control did not render its model.");
            Assert.Null(panel.PlotModel.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task ReusedText_IsPixelIdenticalIncludingUnicodeRotationClippingAndDpi() => PlotRegressionTests.OnDispatcher(() =>
    {
        var standardCanvas = new Canvas { Width = 500, Height = 200, Background = Brushes.White };
        var cachedCanvas = new Canvas { Width = 500, Height = 200, Background = Brushes.White };
        var standard = new CanvasRenderContext(standardCanvas); var cached = new CachedCanvasRenderContext(cachedCanvas);
        foreach (var dpi in new[] { 1d, 1.5, 2d })
        foreach (var offset in new[] { 0d, 10d, 0d })
        {
            void Draw(Canvas canvas, CanvasRenderContext context)
            {
                canvas.Children.Clear(); context.DpiScale = dpi; context.VisualOffset = new Point(offset, 0);
                context.SetToolTip("Test " + offset);
                var texts = new[] { "Tijd [s] 123,456", "CAN Δ µ Ω → ² é", "測定 العربية", "Regel 1\nRegel 2" };
                for (var i = 0; i < texts.Length; i++)
                {
                    var size = context.MeasureText(texts[i], "Segoe UI", 13, 400);
                    Assert.Equal(standard.MeasureText(texts[i], "Segoe UI", 13, 400), size);
                    context.PushClip(new OxyRect(0, 0, 450, 180));
                    context.DrawText(new ScreenPoint(100 + offset, 25 + i * 40), texts[i], OxyColors.DarkBlue,
                        "Segoe UI", 13, 400, i == 1 ? 15 : 0,
                        OxyPlot.HorizontalAlignment.Center, OxyPlot.VerticalAlignment.Middle, new OxySize(180, 40));
                    context.PopClip();
                }
                canvas.Measure(new Size(500, 200)); canvas.Arrange(new Rect(0, 0, 500, 200)); canvas.UpdateLayout();
            }
            // Repeat identical frames to exercise reuse, then change position/DPI above.
            for (var frame = 0; frame < 2; frame++)
            {
                Draw(standardCanvas, standard); Draw(cachedCanvas, cached);
                byte[] Pixels(Canvas canvas)
                {
                    var bitmap = new RenderTargetBitmap(500, 200, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
                    var bytes = new byte[500 * 200 * 4]; bitmap.CopyPixels(bytes, 500 * 4, 0); return bytes;
                }
                // Warm WPF's raster cache before comparing; the first rotated-glyph
                // capture can differ slightly even before any label has been reused.
                _ = Pixels(standardCanvas); _ = Pixels(cachedCanvas);
                var expected = Pixels(standardCanvas); var actual = Pixels(cachedCanvas);
                var baselineRepeat = Pixels(standardCanvas);
                Assert.True(expected.SequenceEqual(baselineRepeat), "The unmodified WPF renderer changed pixels between identical captures.");
                Assert.True(expected.SequenceEqual(actual),
                    $"DPI={dpi}, offset={offset}, frame={frame}: {expected.Zip(actual).Count(p => p.First != p.Second)} channels differ; max={expected.Zip(actual).Max(p => Math.Abs(p.First-p.Second))}.");
            }
        }
        return Task.CompletedTask;
    });
}
