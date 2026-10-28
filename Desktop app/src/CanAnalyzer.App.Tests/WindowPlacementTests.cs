using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CanAnalyzer.App.Infrastructure;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public Task RestoredNativeWindowFitsAnActualMonitorWorkArea()
        => PlotRegressionTests.OnDispatcher(async () =>
        {
            var window = new Window { MinWidth = 1200, MinHeight = 760, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                WindowPlacement.Restore(window, 413.7, -1158.2857, 1461.7, 865.1);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var handle = new WindowInteropHelper(window).Handle;
                Assert.True(GetWindowRect(handle, out var rect));
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                Assert.True(GetMonitorInfo(MonitorFromWindow(handle, 2), ref info));
                Assert.True(info.WorkArea.ToRect().Contains(rect.ToRect()),
                    $"Restored native bounds {rect.ToRect()} exceed monitor work area {info.WorkArea.ToRect()}.");
                Assert.True(window.MinWidth <= window.ActualWidth + 1);
                Assert.True(window.MinHeight <= window.ActualHeight + 1);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(-1920, 0)]
    [InlineData(0, -1080)]
    public void ValidNegativeSecondaryPositionIsPreserved(double left, double top)
    {
        Rect[] monitors = [new(0, 0, 1920, 1040), new(left, top, 1920, 1040)];
        var saved = new Rect(left + 40, top + 50, 1462, 865);
        Assert.Equal(saved, WindowPlacement.Constrain(saved, monitors, new Size(1200, 760)));
    }

    [Fact]
    public void DisconnectedUpperMonitorRestoresToVisibleWorkArea()
    {
        var saved = new Rect(413.7, -1158.2857, 1461.7, 865.1);
        var area = new Rect(0, 0, 1920, 1040);
        var restored = WindowPlacement.Constrain(saved, [area], new Size(1200, 760));
        Assert.True(area.Contains(restored));
        Assert.Equal(0, restored.Top);
        Assert.Equal(saved.Left, restored.Left);
        Assert.Equal(saved.Size, restored.Size);
    }

    [Fact]
    public void VirtualDesktopHoleUsesNearestActualMonitor()
    {
        Rect[] monitors = [new(0, 0, 1920, 1040), new(2400, -1080, 1920, 1040)];
        var restored = WindowPlacement.Constrain(new Rect(2300, 120, 1400, 800), monitors, new Size(1200, 760));
        Assert.True(monitors[1].Contains(restored));
        Assert.False(monitors[0].Contains(restored));
    }

    [Fact]
    public void GreatestOverlapRetainsSecondaryMonitorForOversizedWindow()
    {
        Rect[] monitors = [new(0, 0, 1920, 1040), new(-1920, 0, 1920, 1040)];
        var restored = WindowPlacement.Constrain(new Rect(-1800, -40, 2800, 1800), monitors, new Size(1200, 760));
        Assert.Equal(monitors[1], restored);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void SizeAndApplicationMinimumCannotExceedWorkAreaAtAnyDpi(double scale)
    {
        var area = new Rect(0, 40, 1280, 680);
        var restored = WindowPlacement.Constrain(new Rect(100, 80, 1462 * scale, 865 * scale), [area],
            new Size(1200 * scale, 760 * scale));
        Assert.True(area.Contains(restored));
        Assert.Equal(680, restored.Height);
        Assert.True(restored.Width <= 1280);
    }

    [Fact]
    public void SmallWindowRespectsMinimumOnSpaciousMonitor()
    {
        var area = new Rect(0, 0, 1920, 1040);
        var restored = WindowPlacement.Constrain(new Rect(10, 20, 400, 200), [area], new Size(1200, 760));
        Assert.Equal(new Rect(10, 20, 1200, 760), restored);
    }

    [Fact]
    public void InvalidStoredCoordinatesAndDimensionsRecoverToFiniteVisibleBounds()
    {
        var area = new Rect(0, 0, 1920, 1040);
        var restored = WindowPlacement.Constrain(new Rect(double.NaN, double.NegativeInfinity, double.NaN, double.PositiveInfinity),
            [area], new Size(1200, 760));
        Assert.True(area.Contains(restored));
        Assert.Equal(new Size(1200, 760), restored.Size);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea, WorkArea;
        public uint Flags;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
