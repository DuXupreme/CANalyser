using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CanAnalyzer.App.Infrastructure;

public static class WindowPlacement
{
    // All geometry below is in the native window's coordinate space. Do not compare
    // monitor pixels with saved WPF device-independent coordinates directly.
    public static void Restore(Window window, double left, double top, double width, double height)
    {
        var minimum = new Size(window.MinWidth, window.MinHeight);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(window);
        var workAreas = GetWorkAreas(dpi);
        var requested = new Rect(left * dpi.DpiScaleX, top * dpi.DpiScaleY,
            PositiveOrDefault(width, 1200) * dpi.DpiScaleX, PositiveOrDefault(height, 800) * dpi.DpiScaleY);
        var bounds = Constrain(requested, workAreas,
            new Size(minimum.Width * dpi.DpiScaleX, minimum.Height * dpi.DpiScaleY));
        ApplyMinimum(window, minimum, SelectWorkArea(bounds, workAreas), dpi);
        window.Width = bounds.Width / dpi.DpiScaleX;
        window.Height = bounds.Height / dpi.DpiScaleY;
        window.Left = bounds.Left / dpi.DpiScaleX;
        window.Top = bounds.Top / dpi.DpiScaleY;
        EnsureVisible(window, handle, minimum);
        // Crossing a monitor can change WPF's DPI and its suggested native bounds.
        // Validate that actual result once more after the layout/DPI messages settle.
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (window.IsVisible && window.WindowState == WindowState.Normal)
                EnsureVisible(window, handle, minimum);
        }));
    }

    public static Rect Constrain(Rect requested, IReadOnlyList<Rect> workAreas, Size minimum)
    {
        var area = SelectWorkArea(requested, workAreas);
        var minWidth = Math.Min(PositiveOrDefault(minimum.Width, 1), area.Width);
        var minHeight = Math.Min(PositiveOrDefault(minimum.Height, 1), area.Height);
        var width = Math.Clamp(PositiveOrDefault(requested.Width, minWidth), minWidth, area.Width);
        var height = Math.Clamp(PositiveOrDefault(requested.Height, minHeight), minHeight, area.Height);
        var left = double.IsFinite(requested.Left) ? requested.Left : area.Left + (area.Width - width) / 2;
        var top = double.IsFinite(requested.Top) ? requested.Top : area.Top + (area.Height - height) / 2;
        return new Rect(Math.Clamp(left, area.Left, area.Right - width),
            Math.Clamp(top, area.Top, area.Bottom - height), width, height);
    }

    private static Rect SelectWorkArea(Rect requested, IReadOnlyList<Rect> workAreas)
    {
        var areas = workAreas.Where(IsValidArea).ToArray();
        if (areas.Length == 0) throw new ArgumentException("At least one valid monitor work area is required.", nameof(workAreas));
        if (!IsValidArea(requested)) return areas[0];
        // Work areas are separate rectangles: the virtual desktop's bounding box
        // may include empty holes between screens, or a disconnected monitor.
        return areas.OrderByDescending(area => IntersectionArea(requested, area))
            .ThenBy(area => DistanceSquared(requested, area)).First();
    }

    private static bool IsValidArea(Rect area) => !area.IsEmpty && double.IsFinite(area.X) && double.IsFinite(area.Y)
        && double.IsFinite(area.Right) && double.IsFinite(area.Bottom) && area.Width > 0 && area.Height > 0;
    private static double PositiveOrDefault(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
    private static double IntersectionArea(Rect first, Rect second)
    {
        var intersection = Rect.Intersect(first, second);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }
    private static double DistanceSquared(Rect first, Rect second)
    {
        var x = Math.Max(0, Math.Max(second.Left - first.Right, first.Left - second.Right));
        var y = Math.Max(0, Math.Max(second.Top - first.Bottom, first.Top - second.Bottom));
        return x * x + y * y;
    }

    private static void ApplyMinimum(Window window, Size minimum, Rect area, DpiScale dpi)
    {
        // The usual application minimum must not make the title bar or window
        // bottom unreachable on a smaller work area or at a higher DPI scale.
        window.MinWidth = Math.Min(minimum.Width, area.Width / dpi.DpiScaleX);
        window.MinHeight = Math.Min(minimum.Height, area.Height / dpi.DpiScaleY);
    }

    private static void EnsureVisible(Window window, IntPtr handle, Size minimum)
    {
        if (!GetWindowRect(handle, out var native)) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var areas = GetWorkAreas(dpi);
        var current = native.ToRect();
        var area = SelectWorkArea(current, areas);
        ApplyMinimum(window, minimum, area, dpi);
        var bounds = Constrain(current, areas, new Size(window.MinWidth * dpi.DpiScaleX, window.MinHeight * dpi.DpiScaleY));
        if (bounds == current) return;
        SetWindowPos(handle, IntPtr.Zero, (int)Math.Ceiling(bounds.Left), (int)Math.Ceiling(bounds.Top),
            (int)Math.Floor(bounds.Width), (int)Math.Floor(bounds.Height), 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
    }

    private static IReadOnlyList<Rect> GetWorkAreas(DpiScale dpi)
    {
        var areas = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeRect __, IntPtr ___) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && IsValidArea(info.WorkArea.ToRect())) areas.Add(info.WorkArea.ToRect());
            return true;
        }, IntPtr.Zero);
        if (areas.Count == 0)
        {
            var fallback = SystemParameters.WorkArea;
            areas.Add(new Rect(fallback.X * dpi.DpiScaleX, fallback.Y * dpi.DpiScaleY,
                fallback.Width * dpi.DpiScaleX, fallback.Height * dpi.DpiScaleY));
        }
        return areas;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea, WorkArea;
        public uint Flags;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr deviceContext, ref NativeRect bounds, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
