using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class MainWindowPlotCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MainCancelCommand_CancelsPlotWorkerAndKeepsBusyUntilItHasExited(bool detached)
        => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var builder = new BlockingBuilder();
        using var dataset = PlotRegressionTests.Dataset(20);
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        var limits = new DisplayLimits();
        using var analysis = new AnalysisViewModel(builder, null!, new NoWindow(), null!, new XAxisSyncService(),
            telemetry, new ActiveUsageViewModel(telemetry), NullLogger<AnalysisViewModel>.Instance, limits);
        using var shell = CreateShell(analysis, telemetry, limits);
        var commandNotifications = 0;
        shell.CancelCommand.CanExecuteChanged += (_, _) => commandNotifications++;
        Assert.False(shell.CancelCommand.CanExecute(null));
        analysis.LoadDataset(dataset);
        analysis.LoadPlotGroups([new PlotGroup { Signals = ["test"] }]);
        var pending = detached ? analysis.OpenPlotsInWindowCommand.ExecuteAsync(null) : analysis.ApplyGroupsCommand.ExecuteAsync(null);
        try
        {
            await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(shell.IsBusy);
            Assert.True(shell.IsAnyBusy);
            Assert.True(shell.CancelCommand.CanExecute(null));
            Assert.True(commandNotifications > 0);
            shell.CancelCommand.Execute(null);
            await builder.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(analysis.IsBusy);
            Assert.True(shell.IsAnyBusy);
            Assert.True(shell.IsBusyIndicatorVisible);
            Assert.False(pending.IsCompleted);
            Assert.Empty(analysis.PlotPanels);
        }
        finally { builder.Release.Set(); }
        var notificationsWhileBusy = commandNotifications;
        await pending;
        Assert.False(analysis.IsBusy);
        Assert.False(shell.IsAnyBusy);
        Assert.False(shell.CancelCommand.CanExecute(null));
        Assert.True(commandNotifications > notificationsWhileBusy);
        Assert.DoesNotContain("mislukt", analysis.PresetStatus);
    });

    [Fact]
    public Task OtherAnalyses_EnableCancel_AndCancelDoesNotClearTheirBusyFlagsPrematurely()
        => PlotRegressionTests.OnDispatcher(() =>
    {
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        var limits = new DisplayLimits();
        using var analysis = new AnalysisViewModel(new PlotModelBuilder(), null!, new NoWindow(), null!,
            new XAxisSyncService(), telemetry, new ActiveUsageViewModel(telemetry),
            NullLogger<AnalysisViewModel>.Instance, limits);
        using var shell = CreateShell(analysis, telemetry, limits);
        shell.JoystickAnalytics.IsBusy = true;
        analysis.ActiveUsage.IsBusy = true;
        Assert.True(shell.IsAnyBusy);
        Assert.True(shell.CancelCommand.CanExecute(null));

        // Shell/import busy remains owned by that operation's finally block.
        shell.IsBusy = true;
        Assert.True(shell.CancelCommand.CanExecute(null));
        shell.CancelCommand.Execute(null);
        Assert.True(shell.IsBusy);
        Assert.True(shell.JoystickAnalytics.IsBusy);
        Assert.True(analysis.ActiveUsage.IsBusy);
        shell.IsBusy = false;
        shell.JoystickAnalytics.IsBusy = false;
        analysis.ActiveUsage.IsBusy = false;
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ImportLoadingPanelIsDelayedAndCancellationAlwaysHidesIt(bool waitForPanel) => PlotRegressionTests.OnDispatcher(async () =>
    {
        var telemetry = new AnalyticsRegressionTests.Telemetry();
        var limits = new DisplayLimits();
        using var analysis = new AnalysisViewModel(new PlotModelBuilder(), null!, new NoWindow(), null!,
            new XAxisSyncService(), telemetry, new ActiveUsageViewModel(telemetry), NullLogger<AnalysisViewModel>.Instance, limits);
        using var shell = CreateShell(analysis, telemetry, limits, new WaitingPipeline());
        var file = System.IO.Path.GetTempFileName();
        try
        {
            shell.LogFilePath = file; shell.DbcFilePath = file;
            var work = shell.LoadAndDecodeCommand.ExecuteAsync(null);
            Assert.True(shell.IsBusy);
            Assert.False(shell.ShowDatasetLoading);
            if (waitForPanel)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!shell.ShowDatasetLoading && DateTime.UtcNow < deadline) await Task.Delay(25);
                Assert.True(shell.ShowDatasetLoading);
            }
            shell.CancelCommand.Execute(null);
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(shell.IsBusy);
            Assert.False(shell.ShowDatasetLoading);
            await Task.Delay(250);
            Assert.False(shell.ShowDatasetLoading);
        }
        finally { System.IO.File.Delete(file); }
    });

    private sealed class WaitingPipeline : ICanAnalysisPipeline
    {
        public async Task<PreparedCanAnalysis> PrepareForReviewAsync(string log, string dbc, IProgress<LoadProgress>? progress, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
        public Task<PreparedCanAnalysis> RetryForReviewAsync(PreparedCanAnalysis previous, string log, string dbc, IProgress<LoadProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<CanDataset> LoadAsync(string log, string dbc, ImportMode mode, IProgress<LoadProgress>? progress, CancellationToken token) => throw new NotSupportedException();
    }

    private static MainWindowViewModel CreateShell(AnalysisViewModel analysis,
        ITelemetryService telemetry, DisplayLimits limits, ICanAnalysisPipeline? pipeline = null)
        => new(pipeline!, null!, null!, null!, null!, null!, null!, new UnexpectedDialogs(), new MemorySettings(), telemetry,
            analysis, new JoystickAnalyticsViewModel(new JoystickAnalyticsService(), telemetry, new ActiveUsageViewModel(telemetry)),
            new RawFramesViewModel(new RawFrameFilterService(), limits), new BusmasterViewModel(limits, telemetry),
            new SettingsDiagnosticsViewModel(new FakeUpdateService(), new UnexpectedDialogs(), telemetry, limits),
            null!, NullLogger<MainWindowViewModel>.Instance);

    private sealed class FakeUpdateService : IUpdateService
    {
        public string CurrentVersion => "0.0.0-test";
        public bool IsInstalled => false;
        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Cancellation tests must not check for application updates.");
        public Task DownloadAndApplyAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Cancellation tests must not download application updates.");
    }

    private sealed class UnexpectedDialogs : IMessageDialogService
    {
        public void ShowInfo(string title, string message) => throw new InvalidOperationException("Unexpected info dialog.");
        public void ShowError(string title, string message) => throw new InvalidOperationException("Unexpected error dialog.");
        public bool Confirm(string title, string message) => throw new InvalidOperationException("Unexpected confirmation dialog.");
    }

    private sealed class MemorySettings : IAppSettingsStore
    {
        public string SettingsPath => string.Empty;
        public AppSettings Load() => new();
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BlockingBuilder : IPlotModelBuilder, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public IReadOnlyList<PlotPanelModel> Build(CanDataset dataset, IReadOnlyList<PlotGroup> groups,
            PlotViewOptions options, IReadOnlyList<PlotViewport?>? viewports = null)
            => throw new InvalidOperationException("Plot work must receive a cancellation token.");
        public IReadOnlyList<PlotPanelModel> Build(CanDataset dataset, IReadOnlyList<PlotGroup> groups,
            PlotViewOptions options, IReadOnlyList<PlotViewport?>? viewports, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test did not release plot worker.");
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }
        public void Dispose() => Release.Dispose();
    }

    private sealed class NoWindow : IPlotWindowService
    {
        public void ShowPlots(IReadOnlyList<PlotPanelModel> panels, PlotViewOptions renderedOptions,
            DisplayLimits displayLimits, bool linkXAxisAcrossPanels, bool linkYAxisAcrossPanels, DateTimeOffset? startTimeUtc,
            Action<double>? cursorChanged = null)
            => throw new InvalidOperationException("Cancelled work must not open a window.");
    }
}
