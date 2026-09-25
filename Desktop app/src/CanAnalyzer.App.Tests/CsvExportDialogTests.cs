using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Export;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class CsvExportDialogTests
{
    [Theory]
    [InlineData(800)]
    [InlineData(600)]
    public Task SelectionFormattingAndLayout(int height) => PlotRegressionTests.OnDispatcher(async () =>
    {
        var a = new SignalIdentity("can1", CanFrameFormat.Classic, false, 0x123, "Parker", "Position");
        var b = a with { Channel = "can2" };
        using var dataset = new DatasetBuilder().Build([], [new(1000, -1, -1, a, 12.345, 12, "mm"), new(2000, -1, -1, b, 9, 9, "mm")],
            [], new DecoderDiagnostics(0, 0, 2, 0, 0, ""));
        var window = new CsvExportDialog(dataset, [a.DisplayLabel])
        { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, Height = height };
        try
        {
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Button Button(string label) => Descendants(window).OfType<Button>().Single(b => Equals(b.Content, label));
            void Click(string label) => Button(label).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Click("Uit analyse");
            Assert.Equal(a, Assert.Single(window.CaptureOptions().Signals!));
            Click("Compact delen");
            Click("Excel (NL)");
            ((ComboBox)window.FindName("TimeMode")).SelectedIndex = 2;
            var options = window.CaptureOptions();
            Assert.Equal(CsvExportOptions.CompactColumns, options.Columns);
            Assert.Equal(";", options.Delimiter); Assert.Equal(",", options.DecimalSeparator); Assert.True(options.IncludeUtf8Bom);
            Assert.Equal(CsvTimeOrigin.FirstExportedSample, options.TimeOrigin);
            ((TextBox)window.FindName("SignalSearch")).Text = "can2";
            Click("Zoekresultaten");
            Assert.Equal(b, Assert.Single(window.CaptureOptions().Signals!));
            Click("Uit analyse");
            ((TextBox)window.FindName("SignalSearch")).Text = "";
            window.Signals[0].IsSelected = false;
            window.Signals[1].IsSelected = false;
            Assert.False(Button("Opslaan als…").IsEnabled);
            Click("Uit analyse");
            Assert.True(Button("Opslaan als…").IsEnabled);
            Click("Voorbeeld vernieuwen");
            var preview = (TextBox)window.FindName("PreviewText");
            for (var i = 0; i < 100 && preview.Text == "Voorbeeld wordt gelezen…"; i++) await Task.Delay(20);
            Assert.Contains("12,345", preview.Text);
            window.UpdateLayout();

            var root = (FrameworkElement)window.Content;
            foreach (var control in new FrameworkElement[] { Button("Opslaan als…"), preview, (ComboBox)window.FindName("TimeMode") })
            {
                var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= root.ActualHeight + 1, $"Clipped control at height {height}: {bounds}");
            }
            var screenshot = Environment.GetEnvironmentVariable("CANALYSER_CSV_PREVIEW_PATH");
            if (height == 800 && !string.IsNullOrEmpty(screenshot))
            {
                var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    var rectangle = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                    drawing.DrawRectangle(window.Background, null, rectangle);
                    drawing.DrawRectangle(new VisualBrush(root), null, rectangle);
                }
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(screenshot); encoder.Save(output);
            }
        }
        finally { window.Close(); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
