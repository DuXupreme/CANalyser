using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class SignalWatchWindowTests
{
    private static readonly SignalIdentity Position = new("1", CanFrameFormat.Classic, false, 42, "Actuator Front", "Gemeten positie");
    private static readonly SignalIdentity Soc = new("2", CanFrameFormat.Classic, false, 81, "BMS", "SOC");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ActivatingAndMovingWatchKeepsDetachedPlotAboveMain(bool pinned) => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset();
        var vm = ViewModel(data);
        var main = Owner();
        var plot = new PlotPanelsWindow([], new(), new(), false, false, null) { ShowActivated = false };
        using var host = new SignalWatchWindowHost(main, vm, new() { AlwaysOnTop = pinned }, () => main.Activate());
        try
        {
            await vm.WaitForWatchValuesAsync();
            var watch = host.Show();
            plot.Show();
            await Settle();
            var mainHandle = new WindowInteropHelper(main).Handle;
            var plotHandle = new WindowInteropHelper(plot).Handle;
            var watchHandle = new WindowInteropHelper(watch).Handle;
            Assert.Null(watch.Owner);
            Assert.DoesNotContain(watch, main.OwnedWindows.Cast<Window>());
            Assert.NotEqual(mainHandle, GetWindow(watchHandle, 4)); // GW_OWNER (WPF may use a hidden taskbar helper).

            // Establish the two independent chart/main windows in known native
            // order without relying on permission to steal foreground focus in CI.
            Assert.True(SetWindowPos(mainHandle, IntPtr.Zero, 0, 0, 0, 0, 0x0013)); // NOSIZE|NOMOVE|NOACTIVATE
            Assert.True(SetWindowPos(plotHandle, IntPtr.Zero, 0, 0, 0, 0, 0x0013));
            AssertAbove(plotHandle, mainHandle);
            for (var repeat = 0; repeat < 3; repeat++)
            {
                watch.Activate();
                watch.Left += 10;
                watch.Top += 5;
                await Settle();
                AssertAbove(plotHandle, mainHandle);
            }
            watch.Close();
            await Settle();
            AssertAbove(plotHandle, mainHandle);
        }
        finally { host.Dispose(); plot.Close(); main.Close(); await vm.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    private static void AssertAbove(IntPtr upper, IntPtr lower)
    {
        // Enumerate only z-order; no external window is changed.
        var seen = new HashSet<IntPtr>();
        for (var cursor = upper; cursor != IntPtr.Zero && seen.Add(cursor); cursor = GetWindow(cursor, 2)) // GW_HWNDNEXT
            if (cursor == lower) return;
        Assert.Fail("Activating/moving Signal Watch raised main above the detached plot.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [Fact]
    public Task HostReusesWindowAndLiveRowsAndBindsPinAndChooseSignals() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset();
        var vm = ViewModel(data);
        var owner = Owner();
        var requests = 0;
        using var host = new SignalWatchWindowHost(owner, vm, new(), () => requests++);
        try
        {
            await vm.WaitForWatchValuesAsync();
            var window = host.Show();
            await Settle();
            Assert.Same(window, host.Show());
            Assert.Same(vm, window.DataContext);
            Assert.True(window.Topmost);
            var grid = (DataGrid)window.FindName("WatchGrid");
            Assert.Same(vm.WatchedSignalRows, grid.ItemsSource);
            Assert.Same(vm.WatchedSignalRows[0], grid.Items[0]);
            Assert.Contains("Meetgat", vm.WatchedSignalRows[0].Status);

            var pin = (CheckBox)window.FindName("AlwaysOnTopToggle");
            Assert.True(pin.IsChecked);
            pin.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            await Settle();
            Assert.False(window.Topmost);
            window.Topmost = true;
            await Settle();
            Assert.True(pin.IsChecked);
            ((Button)window.FindName("ChooseSignalsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, requests);

            vm.SetWatchCursorAt(4);
            await vm.WaitForWatchValuesAsync();
            Assert.Equal("125", vm.WatchedSignalRows[0].PhysicalValue);
            Assert.Same(vm.WatchedSignalRows[0], grid.Items[0]);
        }
        finally { host.Dispose(); owner.Close(); await vm.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    [Fact]
    public Task ClosingAndReopeningPreservesGeometryPinSelectionAndViewModel() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset();
        var vm = ViewModel(data);
        var options = new SignalWatchWindowOptions();
        var owner = Owner();
        using var host = new SignalWatchWindowHost(owner, vm, options, () => { });
        try
        {
            await vm.WaitForWatchValuesAsync();
            var first = host.Show();
            await Settle();
            first.Width = 590; first.Height = 300; first.Topmost = false;
            await Settle();
            host.CapturePlacement();
            var expected = new Rect(options.Left!.Value, options.Top!.Value, options.Width, options.Height);
            var rows = vm.WatchedSignalRows;
            first.Close();
            Assert.Null(first.DataContext);
            Assert.Equal(new[] { Position, Soc }, vm.CaptureSignalWatchOptions().Signals);
            using (data.AcquireReadLease()) { } // The presentation window does not own the dataset.
            vm.SetWatchCursorAt(4);
            await vm.WaitForWatchValuesAsync();
            Assert.Equal("125", rows[0].PhysicalValue);

            var reopened = host.Show();
            await Settle();
            Assert.NotSame(first, reopened);
            Assert.Same(vm, reopened.DataContext);
            Assert.Same(rows, vm.WatchedSignalRows);
            Assert.False(reopened.Topmost);
            Assert.InRange(Math.Abs(reopened.Left - expected.Left), 0, 1);
            Assert.InRange(Math.Abs(reopened.Top - expected.Top), 0, 1);
            Assert.InRange(Math.Abs(reopened.ActualWidth - expected.Width), 0, 1);
            Assert.InRange(Math.Abs(reopened.ActualHeight - expected.Height), 0, 1);
        }
        finally { host.Dispose(); owner.Close(); await vm.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    [Fact]
    public Task OwnerCloseDisposesHostAndPopupWithoutClearingSharedSelection() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var data = Dataset();
        var vm = ViewModel(data);
        var owner = Owner();
        using var host = new SignalWatchWindowHost(owner, vm, new(), () => { });
        try
        {
            await vm.WaitForWatchValuesAsync();
            var window = host.Show();
            await Settle();
            owner.Close();
            await Settle();
            Assert.False(window.IsVisible);
            Assert.Null(window.DataContext);
            Assert.Throws<ObjectDisposedException>(() => host.Show());
            Assert.Equal(new[] { Position, Soc }, vm.CaptureSignalWatchOptions().Signals);
            vm.SetWatchCursorAt(4);
            await vm.WaitForWatchValuesAsync();
            Assert.Equal("125", vm.WatchedSignalRows[0].PhysicalValue);
        }
        finally { host.Dispose(); owner.Close(); await vm.CancelAndWaitAsync(); vm.ClearDataset(); }
    });

    [Fact]
    public Task ClosingPopupDoesNotCancelAnInFlightWatchRead() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var store = new BlockingSamples();
        using var data = Dataset(store);
        var vm = new BusmasterViewModel(new DisplayLimits());
        vm.ApplySignalWatchOptions(new() { Signals = [Position], LatestValues = true });
        var owner = Owner();
        using var host = new SignalWatchWindowHost(owner, vm, new(), () => { });
        try
        {
            var window = host.Show();
            vm.LoadDataset(data);
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = vm.WaitForWatchValuesAsync();
            window.Close();
            Assert.False(pending.IsCompleted);
            Assert.False(store.Disposed);
            store.Release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var row = Assert.Single(vm.WatchedSignalRows);
            Assert.Equal("120", row.PhysicalValue);
            Assert.Equal("Laatste sample in opname", row.Status);
            Assert.False(store.Disposed);
            Assert.Same(row, Assert.Single(vm.SignalWatchRows));
            Assert.Same(vm, host.Show().DataContext);
        }
        finally
        {
            store.Release.Set(); host.Dispose(); owner.Close();
            await vm.CancelAndWaitAsync(); vm.ClearDataset();
        }
    });

    [Theory]
    [InlineData(640, 340)]
    [InlineData(420, 220)]
    public Task CompactWindowRendersLiveGapRowWithoutBindingErrors(int width, int height) => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var errors = new StringWriter();
        using var listener = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        using var data = Dataset();
        var vm = ViewModel(data);
        var window = new SignalWatchWindow(vm, new() { Width = width, Height = height, AlwaysOnTop = false }) { ShowActivated = false };
        try
        {
            await vm.WaitForWatchValuesAsync();
            window.Show();
            await Settle();
            var grid = (DataGrid)window.FindName("WatchGrid");
            Assert.Equal(2, grid.Items.Count);
            grid.ScrollIntoView(grid.Items[0]);
            window.UpdateLayout();
            await Settle();
            Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(grid.ActualHeight > 35, "The minimum window must retain room for a data row and scrolling.");
            var text = Descendants<TextBlock>((DependencyObject)window.Content).Select(block => block.Text).ToArray();
            Assert.Contains(text, value => value.Contains("Meetgat", StringComparison.Ordinal));
            Assert.Contains("120", text);
            Assert.Contains("2 signalen gevolgd.", text);
            RenderContent(window, $"signal-watch-window-{width}x{height}.png");
            listener.Flush();
            Assert.DoesNotContain("BindingExpression path error", errors.ToString());
            Assert.DoesNotContain("Cannot find source", errors.ToString());
        }
        finally
        {
            window.Close(); await vm.CancelAndWaitAsync(); vm.ClearDataset();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    });

    private static Window Owner()
    {
        var owner = new Window { Width = 900, Height = 650, ShowActivated = false, ShowInTaskbar = false, Title = "Signal Watch test owner" };
        owner.Show();
        return owner;
    }
    private static BusmasterViewModel ViewModel(CanDataset data)
    {
        var vm = new BusmasterViewModel(new DisplayLimits());
        vm.ApplySignalWatchOptions(new() { Signals = [Position, Soc] });
        vm.LoadDataset(data); vm.SetWatchCursorAt(2);
        return vm;
    }
    private static Task Settle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static CanDataset Dataset(IReadOnlyList<DecodedSignalSample>? samples = null) => new()
    {
        RawFrames = [], DecodedSamples = samples ?? [new(0, 0, 0, Position, 120, 120, "mm"),
            new(0, 1, 1, Soc, 82, 82, "%"), new(4_000_000_000, 2, 2, Position, 125, 125, "mm")],
        MessageSummaries = [], SignalLabels = [], SignalSeriesByLabel = new Dictionary<string, SignalSeries>(),
        SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>(), Diagnostics = new(0, 0, 0, 0, 0, ""),
        ImportReport = new("watch test", 0, 0, 0, 0, [])
        { GapsByChannel = new Dictionary<string, IReadOnlyList<MeasurementGap>> { ["1"] = [new(1, 3)], ["2"] = [] } }
    };
    private static void RenderContent(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("CANALYSER_IMPLEMENTATION_VISUALS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        Assert.True(width > 0 && height > 0);
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        // The content retains its parent margin offset, so include that margin
        // in the bitmap to capture the complete client area without clipping.
        bitmap.Render(background); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); png.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
    {
        if (element is T value) yield return value;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(element, index))) yield return child;
    }

    private sealed class BlockingSamples : IReadOnlyList<DecodedSignalSample>, ISignalSampleLookup, IFrameSampleLookup, IDisposable
    {
        private readonly DecodedSignalSample _sample = new(0, 0, 0, Position, 120, 120, "mm");
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool Disposed { get; private set; }
        public int Count => 1;
        public DecodedSignalSample this[int index] => index == 0 ? _sample : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<DecodedSignalSample> GetEnumerator() { yield return _sample; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IReadOnlyList<SignalSampleSummary> GetSignalSummaries() => [new(1, 120, 120, _sample)];
        public IEnumerable<SignalSeriesPoint> ReadSignalSeries(SignalIdentity identity) => [new(0, 0, 120)];
        public SignalSeriesPoint? FindSignalPointAtOrBefore(SignalIdentity identity, long timestampNanoseconds)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test watch reader was not released.");
            ObjectDisposedException.ThrowIf(Disposed, this);
            return new(0, 0, 120);
        }
        public bool TryGetFrameSummary(long frameIndex, out string messageName, out int sampleCount)
        { messageName = Position.MessageName; sampleCount = 1; return true; }
        public IReadOnlyList<DecodedSignalSample> GetFrameSamples(long frameIndex) => [_sample];
        public void Dispose() { if (Disposed) return; Disposed = true; Release.Dispose(); }
    }
}
