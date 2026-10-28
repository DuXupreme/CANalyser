using System.Windows;
using System.Windows.Input;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;

namespace CanAnalyzer.App.Views;

public partial class SignalWatchWindow : Window
{
    public static RoutedUICommand OpenCommand { get; } = new("Signal Watch openen", nameof(OpenCommand), typeof(SignalWatchWindow));
    private readonly SignalWatchWindowOptions _options;
    public event EventHandler? ChooseSignalsRequested;

    public SignalWatchWindow(BusmasterViewModel viewModel, SignalWatchWindowOptions options)
    {
        InitializeComponent();
        DataContext = viewModel;
        _options = options;
        Topmost = options.AlwaysOnTop;
        Width = double.IsFinite(options.Width) && options.Width > 0 ? Math.Clamp(options.Width, MinWidth, 1600) : 640;
        Height = double.IsFinite(options.Height) && options.Height > 0 ? Math.Clamp(options.Height, MinHeight, 1200) : 340;
        Loaded += OnLoaded;
        Closing += (_, _) => CapturePlacement();
        Closed += (_, _) =>
        {
            ChooseSignalsRequested = null;
            DataContext = null;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var left = _options.Left ?? ((Owner?.Left ?? 0) + Math.Max(0, (Owner?.ActualWidth ?? Width) - Width - 24));
        var top = _options.Top ?? ((Owner?.Top ?? 0) + 100);
        WindowPlacement.Restore(this, left, top, Width, Height);
    }

    public void CapturePlacement()
    {
        _options.AlwaysOnTop = Topmost;
        if (!IsLoaded) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0) return;
        _options.Left = bounds.Left; _options.Top = bounds.Top;
        _options.Width = bounds.Width; _options.Height = bounds.Height;
    }

    private void OnChooseSignals(object sender, RoutedEventArgs e) => ChooseSignalsRequested?.Invoke(this, EventArgs.Empty);
}
