using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class OnlineLogsDateSelectionTests
{
    [Fact]
    public Task WinterTimeSelection_Passes25HourRange_AndDisplaysBothRepeatedTimes() =>
        PlotRegressionTests.OnDispatcher(async () =>
        {
            var netherlands = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
            var service = new RecordingService();
            var window = new OnlineLogsWindow(service, new EmptyHistoryStore(), netherlands)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false
            };
            try
            {
                Named<ComboBox>(window, "MachineBox").SelectedValue = "machine-3";
                Named<DatePicker>(window, "FromPicker").SelectedDate = new DateTime(2026, 10, 25);
                Named<DatePicker>(window, "ToPicker").SelectedDate = new DateTime(2026, 10, 25);
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                Assert.Equal(("machine-3", Utc(24, 22), Utc(25, 23)), Assert.Single(service.Requests));
                Assert.Equal(new[] { "summer-offset", "winter-offset" }, window.Rows.Select(row => row.Key));
                Assert.Equal(service.Files.Select(file => file.RecordedAt), window.Rows.Select(row => row.RecordedAt));
                Assert.All(window.Rows, row => Assert.True(row.IsSelected));

                // Refresh the neighbouring ordinary day through the actual UI event as well.
                Named<DatePicker>(window, "FromPicker").SelectedDate = new DateTime(2026, 10, 26);
                Named<DatePicker>(window, "ToPicker").SelectedDate = new DateTime(2026, 10, 26);
                Named<Button>(window, "RefreshButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                Assert.Equal(2, service.Requests.Count);
                Assert.Equal(("machine-3", Utc(25, 23), Utc(26, 23)), service.Requests[1]);
                Assert.Empty(window.Rows);
            }
            finally { window.Close(); }
        });

    private static DateTimeOffset Utc(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    private static T Named<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private sealed class EmptyHistoryStore : IOnlineLogSelectionHistoryStore
    {
        public IReadOnlyList<OnlineLogSelectionHistoryEntry> Load() => [];
        public Task RememberAsync(OnlineLogSelectionHistoryEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingService : IOnlineLogService
    {
        public List<(string Machine, DateTimeOffset From, DateTimeOffset To)> Requests { get; } = [];
        public OnlineLogFile[] Files { get; } =
        [
            File("summer-offset", 2),
            File("winter-offset", 1)
        ];
        public string? AuthenticationUserName => null;
        public bool RememberCredentials => false;
        public void SetCredentials(string username, string password, bool rememberFor30Days = false) => throw new NotSupportedException();
        public void ClearCredentials() => throw new NotSupportedException();

        public Task<OnlineLogQueryResult> GetLogsAsync(string machineId, DateTimeOffset fromUtc,
            DateTimeOffset toUtc, CancellationToken cancellationToken)
        {
            Requests.Add((machineId, fromUtc, toUtc));
            return Task.FromResult(new OnlineLogQueryResult(
                Files.Where(file => file.RecordedAt >= fromUtc && file.RecordedAt < toUtc).ToArray(), false, 10_000, 0));
        }

        public Task<string> DownloadArchiveAsync(IReadOnlyList<OnlineLogSelection> files,
            IProgress<OnlineDownloadProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static OnlineLogFile File(string key, int offsetHours) => new(
            key, key + ".MF4", "Vlindermachine 3", "48EDFD35", "00000008",
            new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(offsetHours)), Utc(27, 12), 100);
    }
}
