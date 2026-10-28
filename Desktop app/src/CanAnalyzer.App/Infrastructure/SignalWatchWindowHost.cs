using System.Windows;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;

namespace CanAnalyzer.App.Infrastructure;

/// <summary>One presentation window per main window; it never owns dataset work.</summary>
public sealed class SignalWatchWindowHost : IDisposable
{
    private readonly Window _owner;
    private readonly BusmasterViewModel _viewModel;
    private readonly SignalWatchWindowOptions _options;
    private readonly Action _chooseSignals;
    private SignalWatchWindow? _window;
    private bool _disposed;

    public SignalWatchWindowHost(Window owner, BusmasterViewModel viewModel, SignalWatchWindowOptions options, Action chooseSignals)
    {
        _owner = owner; _viewModel = viewModel; _options = options; _chooseSignals = chooseSignals;
        _owner.Closed += OnOwnerClosed;
    }

    public SignalWatchWindow Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is null)
        {
            _window = new SignalWatchWindow(_viewModel, _options) { Owner = _owner };
            _window.ChooseSignalsRequested += OnChooseSignals;
            _window.Closed += OnWindowClosed;
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        return _window;
    }

    public void CapturePlacement() => _window?.CapturePlacement();
    private void OnChooseSignals(object? sender, EventArgs e) => _chooseSignals();
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not SignalWatchWindow window) return;
        window.ChooseSignalsRequested -= OnChooseSignals;
        window.Closed -= OnWindowClosed;
        if (ReferenceEquals(window, _window)) _window = null;
    }
    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Closed -= OnOwnerClosed;
        _window?.Close();
    }
}
