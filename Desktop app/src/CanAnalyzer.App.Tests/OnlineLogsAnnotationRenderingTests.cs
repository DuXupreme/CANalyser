using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class OnlineLogsAnnotationRenderingTests
{
    [Fact]
    public Task SharedAnnotationsRemainVisibleOnRequestAndReceiptFilterUsesExplicitBasis() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var service = new Source();
        var window = new OnlineLogsWindow(service, new History()) { Left = -32000, Top = -32000, Width = 1280, Height = 760,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show();
            for (var attempt = 0; attempt < 100 && window.Rows.Count == 0; attempt++) await Task.Delay(10);
            Assert.Equal(3, window.Rows.Count);
            var grid = (DataGrid)window.FindName("LogsGrid");
            Assert.Equal(2, grid.Items.Count);
            ((CheckBox)window.FindName("ShowExcludedBox")).IsChecked = true;
            Assert.Equal(3, grid.Items.Count);
            Assert.Contains(window.Rows, row => row.AnnotationDisplay.Contains("Sensor los"));
            ((ComboBox)window.FindName("TimeBasisBox")).SelectedValue = "uploaded";
            ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("uploaded", service.LastBasis);
            Assert.True(((Button)window.FindName("MarkExcludedButton")).IsEnabled);
            window.UpdateLayout();
            if (Environment.GetEnvironmentVariable("CANALYSER_IMPLEMENTATION_VISUALS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                var content = (FrameworkElement)window.Content;
                window.Width = 1800; window.Height = 1100;
                content.Margin = new Thickness(0);
                const int width = 1400, height = 760;
                content.Width = width; content.Height = height;
                content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.Background);
                content.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen())
                {
                    drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                    drawing.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
                }
                image.Render(background);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(directory, "online-log-annotations.png")); encoder.Save(output);
            }
        }
        finally { window.Close(); }
    });

    private sealed class History : IOnlineLogSelectionHistoryStore
    {
        public IReadOnlyList<OnlineLogSelectionHistoryEntry> Load() => [];
        public Task RememberAsync(OnlineLogSelectionHistoryEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Source : IOnlineLogService
    {
        public string? AuthenticationUserName => "Testgebruiker";
        public bool RememberCredentials => false;
        public string? LastBasis { get; private set; }
        public void SetCredentials(string username, string password, bool rememberFor30Days = false) { }
        public void ClearCredentials() { }
        public Task<OnlineLogQueryResult> GetLogsAsync(string machineId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken) =>
            GetLogsAsync(machineId, fromUtc, toUtc, "recording", cancellationToken);
        public Task<OnlineLogQueryResult> GetLogsAsync(string machineId, DateTimeOffset fromUtc, DateTimeOffset toUtc, string timeBasis, CancellationToken cancellationToken)
        {
            LastBasis = timeBasis;
            var files = Enumerable.Range(1, 3).Select(i => new OnlineLogFile($"48EDFD35/session/{i:D8}.MF4", $"{i:D8}.MF4", "Testmachine", "48EDFD35", "session",
                fromUtc.AddMinutes(i), toUtc.AddHours(-1).AddMinutes(i), 123456)
            { ObjectVersion = "test-v1", Annotation = new(i == 2, i == 2 ? "Sensor los tijdens deze meting" : "", i == 2 ? "test-r1" : null) }).ToArray();
            return Task.FromResult(new OnlineLogQueryResult(files, false, 100, 0) { AnnotationsAvailable = true, DownloadManifestVersion = 2 });
        }
        public Task<string> DownloadArchiveAsync(IReadOnlyList<OnlineLogSelection> files, IProgress<OnlineDownloadProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
