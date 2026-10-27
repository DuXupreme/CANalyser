using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Wpf;

namespace CanAnalyzer.App.Views;

/// <summary>
/// Cursor state is separate from the graph model: moving a cursor must not update axes,
/// resample signals or redraw their geometry. OxyPlot's own annotations still do the
/// drawing, preserving axis selection, clipping, Unicode labels and export appearance.
/// All state changes and rendering belong to the owning UI dispatcher.
/// </summary>
public static class PlotCursorAnnotations
{
    internal sealed class State
    {
        private readonly PlotModel _model;
        private readonly HashSet<Annotation> _owned = new(ReferenceEqualityComparer.Instance);
        private int _scopeDepth;
        private bool _synchronizing;

        public State(PlotModel model)
        {
            _model = model;
#pragma warning disable CS0618
            model.Updating += (_, _) => Synchronize();
#pragma warning restore CS0618
        }

        public IReadOnlyList<Annotation> Annotations { get; private set; } = [];
        public bool UsesFallback => _model.PlotView is { } view && view is not CachedPlotView &&
            (view is not PlotViewBase wpf || ReferenceEquals(wpf.Model, _model));
        public event Action? Changed;
        public void Notify() => Changed?.Invoke();

        public void Replace(IReadOnlyList<Annotation> annotations)
        {
            Annotations = annotations.Distinct<Annotation>(ReferenceEqualityComparer.Instance).ToArray();
            Synchronize();
        }

        public void Synchronize()
        {
            if (_synchronizing) return;
            _synchronizing = true;
            try
            {
                if (_model.PlotView is PlotViewBase view &&
                    !BindingOperations.IsDataBound(view, ObservedModelProperty))
                {
                    // A self-binding follows ordinary PlotView.Model replacements without
                    // a globally rooted DependencyPropertyDescriptor event subscription.
                    BindingOperations.SetBinding(view, ObservedModelProperty, new Binding
                    {
                        Path = new PropertyPath(PlotViewBase.ModelProperty),
                        RelativeSource = new RelativeSource(RelativeSourceMode.Self),
                        Mode = BindingMode.OneWay
                    });
                }

                var include = _scopeDepth > 0 || UsesFallback;
                foreach (var annotation in _owned.ToArray())
                {
                    if (include && Annotations.Contains(annotation)) continue;
                    _model.Annotations.Remove(annotation);
                    _owned.Remove(annotation);
                }
                if (!include) return;
                foreach (var annotation in Annotations)
                {
                    if (_model.Annotations.Contains(annotation)) continue;
                    _model.Annotations.Add(annotation);
                    _owned.Add(annotation);
                }
            }
            finally { _synchronizing = false; }
        }

        public void EnterScope()
        {
            _scopeDepth++;
            try { Synchronize(); }
            catch
            {
                _scopeDepth--;
                Synchronize();
                throw;
            }
        }

        public void ExitScope()
        {
            _scopeDepth--;
            Synchronize();
        }
    }

    private static readonly ConditionalWeakTable<PlotModel, State> States = new();
    private static readonly DependencyProperty ObservedModelProperty = DependencyProperty.RegisterAttached(
        "ObservedModel", typeof(PlotModel), typeof(PlotCursorAnnotations),
        new PropertyMetadata(null, OnViewModelChanged));

    internal static State For(PlotModel model) => States.GetValue(model, value => new(value));

    private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        Refresh(args.OldValue as PlotModel);
        Refresh(args.NewValue as PlotModel);

        static void Refresh(PlotModel? model)
        {
            if (model is null || !States.TryGetValue(model, out var state)) return;
            state.Synchronize();
            state.Notify();
        }
    }

    public static void Set(PlotModel model, IReadOnlyList<Annotation> annotations)
    {
        var state = For(model);
        // Only annotations inserted by this state are ever removed. Cached views keep
        // their graph model untouched, except during a synchronous overlay/export scope.
        state.Replace(annotations);
        if (state.UsesFallback) model.InvalidatePlot(false);
        state.Notify();
    }

    internal static void PlotRendered(PlotModel? model)
    {
        if (model is not null && States.TryGetValue(model, out var state)) state.Notify();
    }

    /// <summary>
    /// Temporarily includes cursor annotations for a synchronous OxyPlot export.
    /// Dispose on the same UI thread, also when exporting fails. Dataset/CSV exports
    /// do not use this visual state and remain unchanged.
    /// </summary>
    public static IDisposable IncludeForExport(PlotModel model) => new Scope(For(model));

    private sealed class Scope : IDisposable
    {
        private readonly State _state;
        private bool _disposed;

        public Scope(State state)
        {
            _state = state;
            state.EnterScope();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _state.ExitScope();
        }
    }
}

/// <summary>A hit-test-transparent WPF layer; its children contain only cursor geometry.</summary>
public sealed class PlotCursorOverlay : Canvas
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(PlotModel), typeof(PlotCursorOverlay),
        new PropertyMetadata(null, (sender, args) => ((PlotCursorOverlay)sender).ChangeModel(
            (PlotModel?)args.OldValue, (PlotModel?)args.NewValue)));

    private PlotCursorAnnotations.State? _state;
    private PlotModel? _subscribedModel;
    private DispatcherOperation? _pending;

    public PlotModel? Model
    {
        get => (PlotModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public PlotCursorOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) => Schedule();
        IsVisibleChanged += (_, _) => Schedule();
    }

    private void ChangeModel(PlotModel? oldModel, PlotModel? newModel)
    {
        Detach();
        Children.Clear();
        if (IsLoaded) Attach();
    }

    private void Attach()
    {
        if (_subscribedModel is not null || Model is not { } model) return;
        _subscribedModel = model;
        _state = PlotCursorAnnotations.For(model);
        _state.Changed += Schedule;
#pragma warning disable CS0618
        model.Updated += OnModelUpdated;
#pragma warning restore CS0618
        Schedule();
    }

    private void Detach()
    {
        if (_state is not null) _state.Changed -= Schedule;
        if (_subscribedModel is not null)
        {
#pragma warning disable CS0618
            _subscribedModel.Updated -= OnModelUpdated;
#pragma warning restore CS0618
        }
        _state = null;
        _subscribedModel = null;
        _pending?.Abort();
        _pending = null;
    }

    private void OnModelUpdated(object? sender, EventArgs args) => Schedule();

    private void Schedule()
    {
        if (!IsLoaded || !IsVisible || _pending is not null || Dispatcher.HasShutdownStarted) return;
        // Model.Updated fires before OxyPlot finishes its render. Defer to use the final
        // axis transforms and plot rectangle, including resizing and linked navigation.
        _pending = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pending = null;
            Draw();
        }));
    }

    private void Draw()
    {
        Children.Clear();
        if (!IsLoaded || !IsVisible || Model is not { PlotView: CachedPlotView } model || _state is null ||
            model.PlotArea.Width <= 0 || model.PlotArea.Height <= 0) return;
        var context = new CanvasRenderContext(this) { DpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip };
        // Adding the small annotation set only while drawing supplies the normal
        // PlotModel/axis ownership expected by OxyPlot. It never requests a graph
        // update, and disposal restores the annotation collection even on failure.
        using var scope = PlotCursorAnnotations.IncludeForExport(model);
        foreach (var annotation in _state.Annotations)
        {
            annotation.EnsureAxes();
            context.PushClip(annotation.GetClippingRect());
            try { annotation.Render(context); }
            finally { context.PopClip(); }
        }
    }
}
