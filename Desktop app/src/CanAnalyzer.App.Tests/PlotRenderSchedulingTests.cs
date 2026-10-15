using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class PlotRenderSchedulingTests
{
    [Fact]
    public Task BurstThroughPlotModel_CoalescesAndPreservesDataRefresh() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var data = new List<DataPoint> { new(0, 0), new(1, 1) };
        var model = Model();
        model.Series.Add(new LineSeries { ItemsSource = data });
        var view = View(model);
        var host = Host(view);
        try
        {
            host.Show(); await Settle();
            var updates = 0;
            model.Updated += (_, _) => updates++;
            data.Add(new(100, 100));
            for (var i = 0; i < 100; i++) model.InvalidatePlot(i == 25);
            Assert.Equal(0, updates);
            await Until(() => updates > 0);
            Assert.Equal(1, updates);
            Assert.True(model.Axes[0].ActualMaximum >= 100, "The coalesced request lost updateData=true.");
            Assert.Null(model.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task LinkedXYChanges_RenderEachAttachedPanelOnce() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var models = new[] { Model(), Model() };
        foreach (var model in models) model.Series.Add(new LineSeries { Points = { new(0, 0), new(10, 10) } });
        var container = new StackPanel();
        foreach (var model in models) container.Children.Add(View(model));
        var host = Host(container);
        try
        {
            host.Show(); await Settle();
            var sync = new XAxisSyncService(); sync.Configure(true, true); sync.Bind(models);
            await Settle();
            var updates = new int[2];
            models[0].Updated += (_, _) => updates[0]++;
            models[1].Updated += (_, _) => updates[1]++;
            models[0].Axes[0].Zoom(2, 8);
            models[0].Axes[1].Zoom(3, 7);
            models[0].InvalidatePlot(false);
            Assert.Equal(new[] { 0, 0 }, updates);
            await Until(() => updates.All(count => count > 0));
            Assert.Equal(new[] { 1, 1 }, updates);
            Assert.Equal(2, models[1].Axes[0].ActualMinimum);
            Assert.Equal(3, models[1].Axes[1].ActualMinimum);
            sync.Bind([]);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task OffscreenPanel_DefersUpdatesAndRefreshesLatestRangeWhenScrolledIntoView() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var model = Model(); model.Series.Add(new LineSeries { Points = { new(0, 0), new(100, 100) } });
        var view = View(model);
        var stack = new StackPanel();
        stack.Children.Add(new Border { Height = 1000 }); stack.Children.Add(view);
        var scroll = new ScrollViewer { Content = stack, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var host = Host(scroll);
        try
        {
            host.Show(); await Settle();
            var updates = 0; model.Updated += (_, _) => updates++;
            for (var i = 0; i < 50; i++) { model.Axes[0].Zoom(i, i + 10); model.InvalidatePlot(false); }
            await Settle();
            Assert.Equal(0, updates);
            scroll.ScrollToBottom();
            await Until(() => updates > 0);
            Assert.Equal(1, updates);
            Assert.Equal(49, model.Axes[0].ActualMinimum);
            Assert.Equal(59, model.Axes[0].ActualMaximum);
            Assert.Null(model.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task ExpensivePanel_YieldsToInputBeforeRenderingTheNextPanel() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var models = new[] { Model(), Model() };
        var container = new StackPanel();
        foreach (var model in models) container.Children.Add(View(model));
        var host = Host(container);
        try
        {
            host.Show(); await Settle();
            var secondUpdates = 0;
            var inputHandled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            models[1].Updated += (_, _) => secondUpdates++;
            models[0].Updated += (_, _) =>
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input,
                    new Action(() => inputHandled.TrySetResult(secondUpdates)));
                Thread.Sleep(12); // Simulate a single expensive panel; input must get the next turn.
            };
            foreach (var model in models) model.InvalidatePlot(false);
            Assert.Equal(0, await inputHandled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await Until(() => secondUpdates > 0);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task DirtyUnloadedView_RefreshesLatestRangeAndDataAfterReload() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var data = new List<DataPoint> { new(0, 0), new(100, 100) };
        var model = Model(); model.Series.Add(new LineSeries { ItemsSource = data });
        var view = View(model);
        var container = new Grid(); container.Children.Add(view);
        var host = Host(container);
        try
        {
            host.Show(); await Settle();
            var updates = 0; model.Updated += (_, _) => updates++;
            model.InvalidatePlot(true);
            container.Children.Remove(view);
            await Until(() => !view.IsLoaded);
            await Settle();
            Assert.Equal(0, updates);

            data.Add(new(1000, 1000));
            model.Axes[0].Zoom(25, 75);
            model.InvalidatePlot(false);
            await Settle();
            Assert.Equal(0, updates);

            container.Children.Add(view);
            await Until(() => view.IsLoaded && updates > 0);
            Assert.Same(model, view.ActualModel);
            Assert.Equal(25, model.Axes[0].ActualMinimum);
            Assert.Equal(75, model.Axes[0].ActualMaximum);
            Assert.True(model.Axes[1].ActualMaximum >= 1000,
                "Reload lost the updateData=true requested before unloading.");
            Assert.Null(model.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task DirtyView_ReparentedBeforeUnload_ResumesInItsNewScrollViewport() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var data = new List<DataPoint> { new(0, 0), new(100, 100) };
        var model = Model(); model.Series.Add(new LineSeries { ItemsSource = data });
        var view = View(model);
        var originalContent = new StackPanel(); originalContent.Children.Add(view);
        var replacementContent = new StackPanel(); replacementContent.Children.Add(new Border { Height = 1000 });
        var originalScroll = new ScrollViewer { Content = originalContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var replacementScroll = new ScrollViewer { Content = replacementContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var container = new Grid();
        container.RowDefinitions.Add(new RowDefinition()); container.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(replacementScroll, 1);
        container.Children.Add(originalScroll); container.Children.Add(replacementScroll);
        var host = Host(container);
        try
        {
            host.Show(); await Settle();
            var updates = 0; model.Updated += (_, _) => updates++;
            data.Add(new(1000, 1000));
            model.Axes[0].Zoom(25, 75);
            model.InvalidatePlot(true);

            // IsLoaded remains true during this synchronous move. IsVisibleChanged
            // runs on insertion before Unloaded/Loaded replace the cached viewport.
            originalContent.Children.Remove(view);
            Assert.True(view.IsLoaded);
            replacementContent.Children.Add(view);
            await Settle();
            Assert.True(view.IsLoaded);
            Assert.Equal(0, updates);

            replacementScroll.ScrollToBottom();
            await Until(() => updates > 0);
            Assert.Equal(1, updates);
            Assert.Equal(25, model.Axes[0].ActualMinimum);
            Assert.Equal(75, model.Axes[0].ActualMaximum);
            Assert.True(model.Axes[1].ActualMaximum >= 1000,
                "Moving to a different viewport lost the pending data refresh.");
            Assert.Null(model.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task PendingRequestAfterModelReplacement_UpdatesOnlyCurrentModel() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var original = Model(); original.Series.Add(new LineSeries { Points = { new(0, 0), new(10, 10) } });
        var view = View(original);
        var host = Host(view);
        try
        {
            host.Show(); await Settle();
            var originalUpdates = 0; original.Updated += (_, _) => originalUpdates++;
            original.InvalidatePlot(true);

            var data = new List<DataPoint> { new(0, 0), new(100, 100) };
            var replacement = Model(); replacement.Series.Add(new LineSeries { ItemsSource = data });
            view.Model = replacement;
            var replacementUpdates = 0; replacement.Updated += (_, _) => replacementUpdates++;
            data.Add(new(1000, 1000));
            replacement.Axes[0].Zoom(40, 60);
            replacement.InvalidatePlot(true);
            replacement.InvalidatePlot(false);
            original.Axes[0].Zoom(9000, 9999);
            original.InvalidatePlot(true);

            await Until(() => replacementUpdates > 0);
            Assert.Equal(0, originalUpdates);
            Assert.Equal(1, replacementUpdates);
            Assert.Null(original.PlotView);
            Assert.Same(replacement, view.ActualModel);
            Assert.Same(view, replacement.PlotView);
            Assert.Equal(40, replacement.Axes[0].ActualMinimum);
            Assert.Equal(60, replacement.Axes[0].ActualMaximum);
            Assert.True(replacement.Axes[1].ActualMaximum >= 1000);
            Assert.Null(replacement.GetLastPlotException());
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task ReentrantRequest_GoesBehindAlreadyQueuedPanels() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var models = new[] { Model(), Model() };
        var container = new StackPanel();
        foreach (var model in models) container.Children.Add(View(model));
        var host = Host(container);
        try
        {
            host.Show(); await Settle();
            var order = new List<int>();
            models[0].Updated += (_, _) =>
            {
                order.Add(0);
                if (order.Count == 1)
                    for (var i = 0; i < 20; i++) models[0].InvalidatePlot(false);
            };
            models[1].Updated += (_, _) => order.Add(1);
            foreach (var model in models) model.InvalidatePlot(false);

            await Until(() => order.Count >= 3);
            await Settle();
            Assert.Equal(new[] { 0, 1, 0 }, order);
        }
        finally { host.Close(); }
    });

    private static PlotModel Model()
    {
        var model = new PlotModel();
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Key = "Y1" });
        return model;
    }
    private static CachedPlotView View(PlotModel model) => new() { Model = model, Width = 480, Height = 250 };
    private static Window Host(UIElement content) => new() { Content = content, Width = 520, Height = 600,
        Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(80);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
