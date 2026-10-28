using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Analysis;
using OxyPlot.Axes;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class ComparisonViewModelTests
{
    [Fact]
    public Task SelectionProducesSideBySideStatisticsAndRelativeTrends_ExcludingConflictUnlocksAggregate()
        => PlotRegressionTests.OnDispatcher(() =>
        {
            var vm = new ComparisonViewModel(() => []);
            var first = Bundle("first", "A", true);
            var second = Bundle("second", "B", false);
            vm.Bundles.Add(new(first)); vm.Bundles.Add(new(second));
            // Machine filtering is the same refresh path used by the view.
            vm.SelectedMachine = "M01";
            Assert.Equal(2, vm.Results.Count);
            Assert.Equal(2, vm.Trend.Series.Count);
            Assert.Single(vm.Trend.Legends);
            Assert.IsType<LinearAxis>(vm.Trend.Axes[0]);
            Assert.False(vm.CanUseVerifiedUtc);
            Assert.Contains("Onbevestigde", vm.AggregateExplanation);
            vm.Bundles[1].Included = false;
            vm.SelectedMachine = "Alle machines";
            Assert.Single(vm.Results);
            Assert.Single(vm.Trend.Series);
            Assert.True(vm.CanUseVerifiedUtc);
            vm.UseVerifiedUtc = true;
            Assert.IsType<DateTimeAxis>(vm.Trend.Axes[0]);
            Assert.Equal("2 V", vm.AggregateValue);
            return Task.CompletedTask;
        });

    [Fact]
    public Task DifferentUnitsRetainIndividualStatisticsWithoutMisleadingSharedTrendAxis()
        => PlotRegressionTests.OnDispatcher(() =>
        {
            var vm = new ComparisonViewModel(() => []);
            var first = Bundle("first", "A", true);
            var second = Bundle("second", "B", true);
            second = second with { Metrics = [second.Metrics[0] with { Unit = "mV", Value = 2000 }] };
            vm.Bundles.Add(new(first)); vm.Bundles.Add(new(second)); vm.SelectedMachine = "M01";
            Assert.Equal(new[] { "V", "mV" }, vm.Results.Select(result => result.Unit));
            Assert.Empty(vm.Trend.Series);
            Assert.Contains("Verschillende eenheden", vm.Trend.Subtitle);
            Assert.Equal("Niet beschikbaar", vm.AggregateValue);
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancelOrDisposeDoesNotPublishLateReaderResults(bool dispose)
        => PlotRegressionTests.OnDispatcher(async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var reads = 0;
            using var vm = new ComparisonViewModel(() => [], (path, token) =>
            {
                Interlocked.Increment(ref reads);
                if (path.EndsWith("pending.zip", StringComparison.Ordinal))
                {
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    started.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test reader was not released.");
                    // Deliberately return after cancellation: publication must still be suppressed.
                }
                return Bundle(path, path, true);
            });
            await vm.LoadBundlesAsync(["existing.zip"]);
            var oldRow = Assert.Single(vm.Bundles);
            var loading = vm.LoadBundlesAsync(["pending.zip"]);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (dispose) vm.Dispose(); else vm.CancelLoadingCommand.Execute(null);
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { release.Set(); }
            await loading;
            Assert.False(vm.IsBusy);
            if (dispose)
            {
                Assert.Empty(vm.Bundles);
                Assert.Empty(vm.Results);
                Assert.Equal("Vergelijking gesloten.", vm.Status);
                var notifications = 0;
                vm.PropertyChanged += (_, _) => notifications++;
                oldRow.Included = false;
                Assert.Equal(0, notifications);
                await vm.LoadBundlesAsync(["after-disposal.zip"]);
                Assert.Equal(2, reads);
                Assert.False(vm.OpenBundlesCommand.CanExecute(null));
            }
            else
            {
                Assert.Same(oldRow, Assert.Single(vm.Bundles));
                Assert.Contains("geannuleerd", vm.Status);
            }
        });

    [Fact]
    public Task ClearResetsUtcAndMachineFiltersBeforeOpeningAnotherPackage()
        => PlotRegressionTests.OnDispatcher(async () =>
        {
            using var vm = new ComparisonViewModel(() => [], (path, _) =>
                Bundle(path, path, !path.EndsWith("new.zip", StringComparison.Ordinal)));
            await vm.LoadBundlesAsync(["old.zip"]);
            vm.SelectedMachine = "M01";
            vm.PeriodStart = new DateTime(2026, 10, 27);
            vm.PeriodEnd = new DateTime(2026, 10, 27);
            vm.UseVerifiedUtc = true;
            Assert.Single(vm.Results);

            vm.ClearBundlesCommand.Execute(null);

            Assert.Null(vm.PeriodStart);
            Assert.Null(vm.PeriodEnd);
            Assert.Equal("Alle machines", vm.SelectedMachine);
            Assert.False(vm.UseVerifiedUtc);
            Assert.Empty(vm.Bundles);
            Assert.Equal("Nog geen analysepakketten geopend.", vm.FilterStatus);

            // Unverified dates must also be visible after clearing the previous comparison.
            await vm.LoadBundlesAsync(["new.zip"]);
            Assert.Single(vm.Results);
            Assert.Single(vm.Metrics);
            Assert.Single(vm.Trend.Series);
            Assert.Equal("1 van 1 geladen pakketten in deze vergelijking.", vm.FilterStatus);
        });

    [Fact]
    public Task EmptyFilterResultsExplainHowToRestoreTheLoadedPackages()
        => PlotRegressionTests.OnDispatcher(async () =>
        {
            using var vm = new ComparisonViewModel(() => [], (path, _) => Bundle(path, path, true));
            await vm.LoadBundlesAsync(["loaded.zip"]);

            vm.PeriodStart = new DateTime(2026, 10, 28);
            Assert.Single(vm.Bundles);
            Assert.Empty(vm.Results);
            Assert.Contains("Geen pakketten passen bij de huidige filters", vm.FilterStatus);
            Assert.Contains("Pas de machine- of datumfilters aan", vm.FilterStatus);

            vm.PeriodEnd = new DateTime(2026, 10, 27);
            Assert.Contains("Ongeldig datumfilter", vm.FilterStatus);
            vm.PeriodStart = null;
            vm.PeriodEnd = null;
            Assert.Single(vm.Results);
            Assert.Equal("1 van 1 geladen pakketten in deze vergelijking.", vm.FilterStatus);

            vm.Bundles[0].Included = false;
            Assert.Empty(vm.Results);
            Assert.Contains("Geen pakketten meegenomen", vm.FilterStatus);
        });

    private static AnalysisComparisonBundle Bundle(string path, string source, bool verified) => new()
    {
        Path = path, SchemaVersion = 3, MachineId = "M01", ClockQuality = verified ? "verified" : "unverified",
        StartUtc = DateTimeOffset.Parse("2026-10-27T08:00:00Z"), StartSeconds = 0, EndSeconds = 10,
        SourceIdentityComplete = true, SourceIds = [source], FullDataset = true,
        Metrics = [new("signal:x", "Voltage", "V", ComparisonAggregation.WeightedMean, 2, 20, 10,
            "same", [new(0, 60, 10, 20)])]
    };
}
