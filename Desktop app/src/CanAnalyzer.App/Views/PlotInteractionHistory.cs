using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Wpf;

namespace CanAnalyzer.App.Views;

/// <summary>Stores axis limits only; undo never reloads or rebuilds signal data.</summary>
internal sealed class PlotInteractionHistory
{
    private readonly Func<IEnumerable<PlotModel>> _models;
    private readonly List<Limit[]> _history = [];
    private record Limit(PlotModel Model, Axis Axis, double Min, double Max);

    public PlotInteractionHistory(FrameworkElement host, Func<IEnumerable<PlotModel>> models)
    {
        _models = models;
        host.PreviewMouseDown += (_, e) => { if (FindPlot(e.OriginalSource as DependencyObject) is { } plot) { Capture(); plot.Focus(); } };
        host.PreviewMouseWheel += (_, e) => { if (FindPlot(e.OriginalSource as DependencyObject) is { } plot) { Capture(); plot.Focus(); } };
        host.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control ||
                Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
            e.Handled = Undo();
        };
    }

    internal bool Undo()
    {
        var modelsNow = _models().ToHashSet();
        while (_history.Count > 0)
        {
            var limits = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            if (limits.Any(l => !modelsNow.Contains(l.Model) || !l.Model.Axes.Contains(l.Axis))) continue;
            if (limits.All(l => l.Min == l.Axis.ActualMinimum && l.Max == l.Axis.ActualMaximum)) continue;
            foreach (var l in limits) l.Axis.Zoom(l.Min, l.Max);
            foreach (var model in modelsNow) model.InvalidatePlot(false);
            return true;
        }
        return false;
    }

    public void Clear() => _history.Clear();

    private void Capture()
    {
        var limits = _models().SelectMany(m => m.Axes.Where(a => double.IsFinite(a.ActualMinimum) && double.IsFinite(a.ActualMaximum))
            .Select(a => new Limit(m, a, a.ActualMinimum, a.ActualMaximum))).ToArray();
        if (limits.Length == 0) return;
        if (_history.Count > 0 && _history[^1].SequenceEqual(limits)) return;
        _history.Add(limits);
        if (_history.Count > 100) _history.RemoveAt(0);
    }

    private static PlotView? FindPlot(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is PlotView plot) return plot;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}

public static class DownsamplingConfirmation
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(DownsamplingConfirmation), new PropertyMetadata(false, OnChanged));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CheckBox box) return;
        if ((bool)e.NewValue) { box.PreviewMouseLeftButtonDown += OnMouse; box.PreviewKeyDown += OnKey; }
        else { box.PreviewMouseLeftButtonDown -= OnMouse; box.PreviewKeyDown -= OnKey; }
    }
    private static bool Cancel(CheckBox box) => box.IsChecked == true && MessageBox.Show(Window.GetWindow(box),
        "Downsampling uitschakelen? Alle meetpunten worden dan getekend. Bij grote logs kan dit lang duren en kan de grafiek tijdelijk niet reageren.",
        "Downsampling uitschakelen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
    private static void OnMouse(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox { IsChecked: true } box) return;
        // A modal dialog consumes mouse-up. Handle the original click completely,
        // then update the bound value only after explicit consent.
        e.Handled = true;
        if (!Cancel(box)) box.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
    }
    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || sender is not CheckBox { IsChecked: true } box) return;
        e.Handled = true;
        if (!Cancel(box)) box.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
    }
}
