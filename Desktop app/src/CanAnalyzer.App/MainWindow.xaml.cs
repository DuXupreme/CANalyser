using System.ComponentModel;
using System.Windows;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;

namespace CanAnalyzer.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _isShuttingDown;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        AddHandler(TimePlotView.CursorMovedEvent, new EventHandler<TimePlotCursorEventArgs>((_, args) =>
            _viewModel.Busmaster.SetWatchCursorAt(args.TimeSeconds)));

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = _viewModel.LoadedSettings;
        WindowPlacement.Restore(this, settings.WindowLeft, settings.WindowTop, settings.WindowWidth, settings.WindowHeight);

        if (_viewModel.LoadedSettings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        e.Cancel = true;

        if (_viewModel.IsBusy)
        {
            _viewModel.CancelCommand.Execute(null);
        }

        var isMaximized = WindowState == WindowState.Maximized;
        var width = isMaximized ? RestoreBounds.Width : ActualWidth;
        var height = isMaximized ? RestoreBounds.Height : ActualHeight;
        var left = isMaximized ? RestoreBounds.Left : Left;
        var top = isMaximized ? RestoreBounds.Top : Top;

        try
        {
            await _viewModel.PersistWindowStateAsync(width, height, left, top, isMaximized, CancellationToken.None);
        }
        catch
        {
            // Never block shutdown when settings persistence fails.
        }

        _isShuttingDown = true;
        Close();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var about = new Views.AboutWindow(_viewModel.SettingsDiagnostics) { Owner = this };
        about.ShowDialog();
    }
}
