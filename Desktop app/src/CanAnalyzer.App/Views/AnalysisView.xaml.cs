using System.Windows.Controls;
using System.Windows.Input;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.ViewModels;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Wpf;

namespace CanAnalyzer.App.Views;

public partial class AnalysisView : UserControl
{
    private DateTime _lastCursorUpdateUtc = DateTime.MinValue;

    public AnalysisView()
    {
        InitializeComponent();
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not PlotView plotView || DataContext is not AnalysisViewModel viewModel)
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed ||
            e.RightButton == MouseButtonState.Pressed ||
            e.MiddleButton == MouseButtonState.Pressed)
        {
            return;
        }

        if (!TryGetTimeAtMouse(plotView, e.GetPosition(plotView), out var time))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastCursorUpdateUtc).TotalMilliseconds < 60)
        {
            return;
        }

        _lastCursorUpdateUtc = now;
        var panel = plotView.DataContext as PlotPanelModel;
        viewModel.SetCursorAt(time, panel);
    }

    private void OnPlotPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not PlotView plotView || DataContext is not AnalysisViewModel viewModel)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left && e.ClickCount >= 2)
        {
            viewModel.ResetAllPlots();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (TryToggleLegendSeries(plotView, e.GetPosition(plotView)))
        {
            if (viewModel.CursorTime.HasValue)
            {
                viewModel.SetCursorAt(viewModel.CursorTime.Value, plotView.DataContext as PlotPanelModel);
            }
            e.Handled = true;
            return;
        }

        if (!TryGetTimeAtMouse(plotView, e.GetPosition(plotView), out var time))
        {
            return;
        }

        var panel = plotView.DataContext as PlotPanelModel;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            viewModel.SetFlagAAt(time, panel);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            viewModel.SetFlagBAt(time, panel);
            e.Handled = true;
        }
    }

    private static bool TryGetTimeAtMouse(PlotView plotView, System.Windows.Point point, out double time)
    {
        time = 0;
        var model = plotView.ActualModel;
        if (model is null)
        {
            return false;
        }

        var xAxis = model.Axes.FirstOrDefault(axis => axis.Position == AxisPosition.Bottom);
        if (xAxis is null)
        {
            return false;
        }

        try
        {
            time = xAxis.InverseTransform(point.X);
            return !double.IsNaN(time) && !double.IsInfinity(time);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryToggleLegendSeries(PlotView plotView, System.Windows.Point point)
    {
        var model = plotView.ActualModel;
        if (model is null)
        {
            return false;
        }

        var screenPoint = new ScreenPoint(point.X, point.Y);
        var inLegend = model.Legends.Any(legend => legend.LegendArea.Contains(screenPoint));
        if (!inLegend)
        {
            return false;
        }

        var hitArgs = new HitTestArguments(screenPoint, 6);
        var hits = model.HitTest(hitArgs).ToList();
        var hitSeries = hits
            .SelectMany(hit => new OxyPlot.Series.Series?[]
            {
                hit.Element as OxyPlot.Series.Series,
                hit.Item as OxyPlot.Series.Series
            })
            .FirstOrDefault(series => series is not null);
        if (hitSeries is null)
        {
            return false;
        }

        hitSeries.IsVisible = !hitSeries.IsVisible;
        model.InvalidatePlot(false);
        return true;
    }
}
