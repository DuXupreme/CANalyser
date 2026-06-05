using OxyPlot;
using OxyPlot.Axes;
using System.Windows.Input;

namespace CanAnalyzer.App.Services;

/// <inheritdoc />
public sealed class XAxisSyncService : IXAxisSyncService
{
    private readonly List<(Axis Axis, PlotModel Model)> _xBindings = [];
    private readonly List<(Axis Axis, PlotModel Model)> _yBindings = [];
    private bool _isSyncingX;
    private bool _isSyncingY;
    private bool _syncXAxis = true;
    private bool _syncYAxis = true;

    public void Configure(bool syncXAxis, bool syncYAxis)
    {
        _syncXAxis = syncXAxis;
        _syncYAxis = syncYAxis;
    }

    public void Bind(IEnumerable<PlotModel> models)
    {
        UnbindAll();

        foreach (var model in models)
        {
            var xAxis = model.Axes.FirstOrDefault(candidate => candidate.Position == AxisPosition.Bottom);
            if (xAxis is not null)
            {
                xAxis.AxisChanged += OnAxisChanged;
                _xBindings.Add((xAxis, model));
            }

            var yAxis = model.Axes.FirstOrDefault(candidate => string.Equals(candidate.Key, "Y1", StringComparison.Ordinal))
                        ?? model.Axes.FirstOrDefault(candidate => candidate.Position == AxisPosition.Left);
            if (yAxis is not null)
            {
                yAxis.AxisChanged += OnAxisChanged;
                _yBindings.Add((yAxis, model));
            }
        }

        ApplyInitialSharedRange(_xBindings, ref _isSyncingX);
    }

    private void OnAxisChanged(object? sender, AxisChangedEventArgs e)
    {
        if (sender is not Axis sourceAxis)
        {
            return;
        }

        if (_syncXAxis &&
            _xBindings.Any(binding => ReferenceEquals(binding.Axis, sourceAxis)))
        {
            SyncRange(sourceAxis, _xBindings, ref _isSyncingX);
            return;
        }

        if (!_syncYAxis || ShouldUseIndividualYScaling())
        {
            return;
        }

        if (_yBindings.Any(binding => ReferenceEquals(binding.Axis, sourceAxis)))
        {
            SyncRange(sourceAxis, _yBindings, ref _isSyncingY);
        }
    }

    private static bool ShouldUseIndividualYScaling()
    {
        return (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
    }

    private static void SyncRange(
        Axis sourceAxis,
        IReadOnlyList<(Axis Axis, PlotModel Model)> bindings,
        ref bool isSyncing)
    {
        if (isSyncing)
        {
            return;
        }

        var min = sourceAxis.ActualMinimum;
        var max = sourceAxis.ActualMaximum;
        if (double.IsNaN(min) || double.IsNaN(max) || max <= min)
        {
            return;
        }

        try
        {
            isSyncing = true;
            foreach (var (axis, model) in bindings)
            {
                if (ReferenceEquals(axis, sourceAxis))
                {
                    continue;
                }

                if (Math.Abs(axis.ActualMinimum - min) < 1e-9 &&
                    Math.Abs(axis.ActualMaximum - max) < 1e-9)
                {
                    continue;
                }

                axis.Zoom(min, max);
                model.InvalidatePlot(false);
            }
        }
        finally
        {
            isSyncing = false;
        }
    }

    private static void ApplyInitialSharedRange(
        IReadOnlyList<(Axis Axis, PlotModel Model)> bindings,
        ref bool isSyncing)
    {
        if (bindings.Count < 2)
        {
            return;
        }

        var minima = bindings
            .Select(binding => binding.Axis.ActualMinimum)
            .Where(value => !double.IsNaN(value))
            .ToList();
        var maxima = bindings
            .Select(binding => binding.Axis.ActualMaximum)
            .Where(value => !double.IsNaN(value))
            .ToList();

        if (minima.Count == 0 || maxima.Count == 0)
        {
            return;
        }

        var min = minima.Min();
        var max = maxima.Max();
        if (max <= min)
        {
            return;
        }

        try
        {
            isSyncing = true;
            foreach (var (axis, model) in bindings)
            {
                axis.Zoom(min, max);
                model.InvalidatePlot(false);
            }
        }
        finally
        {
            isSyncing = false;
        }
    }

    private void UnbindAll()
    {
        foreach (var (axis, _) in _xBindings)
        {
            axis.AxisChanged -= OnAxisChanged;
        }

        foreach (var (axis, _) in _yBindings)
        {
            axis.AxisChanged -= OnAxisChanged;
        }

        _xBindings.Clear();
        _yBindings.Clear();
    }
}
