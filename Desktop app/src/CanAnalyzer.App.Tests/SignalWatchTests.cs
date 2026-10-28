using System.Numerics;
using System.Text.Json;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Storage;
using CanAnalyzer.Core.Interfaces;
using System.Collections;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class SignalWatchTests
{
    private static readonly SignalIdentity A = new("1", CanFrameFormat.Classic, false, 42, "Actuator", "Position");
    private static readonly SignalIdentity B = A with { Channel = "2" };
    private static DecodedSignalSample Sample(long ns, long frame, SignalIdentity identity, double value) =>
        new(ns, frame, frame, identity, value, new BigInteger(frame + 100), "mm");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviousLookupIsExactPerChannelAndNeverBorrowsAFutureSample(bool disk)
    {
        var samples = new[] { Sample(10, 0, A, 1), Sample(20, 1, B, 8), Sample(30, 2, A, 3), Sample(30, 3, A, 4) };
        using var store = disk ? new DiskBackedDecodedSampleStore() : null;
        if (store is not null) { foreach (var sample in samples) store.Append(sample); store.Complete(); }
        using var dataset = Dataset(store is null ? samples : store);
        var lookup = new SignalWatchLookup(dataset);
        Assert.Null(lookup.AtOrBefore(A, 9));
        Assert.Equal(samples[0], lookup.AtOrBefore(A, 29));
        Assert.Equal(samples[3], lookup.AtOrBefore(A, 30));
        Assert.Equal(samples[1], lookup.AtOrBefore(B, 30));
        Assert.Equal(samples[3].RawValue, lookup.AtOrBefore(A, long.MaxValue)!.RawValue);
    }

    [Fact]
    public void IndexedLookupHandlesBlockBoundariesAndUnorderedLegacyTimestamps()
    {
        using var store = new DiskBackedDecodedSampleStore();
        for (var i = 0; i < 800; i++) store.Append(Sample(i == 700 ? 12 : i, i, A, i));
        store.Complete();
        using var dataset = Dataset(store);
        var lookup = new SignalWatchLookup(dataset);
        Assert.Equal(700, lookup.AtOrBefore(A, 12)!.FrameIndex);
        Assert.Equal(511, lookup.AtOrBefore(A, 511)!.FrameIndex);
        Assert.Equal(799, lookup.AtOrBefore(A, 1000)!.FrameIndex);
    }

    [Fact]
    public async Task SavedWatchUsesCursorTimeAndMarksOnlyTheAffectedChannelGap()
    {
        using var dataset = Dataset([Sample(0, 0, A, 1), Sample(0, 1, B, 2), Sample(4_000_000_000, 2, A, 9)],
            new ImportReport("test", 0, 0, 0, 0, []) { GapsByChannel = new Dictionary<string, IReadOnlyList<MeasurementGap>> { ["1"] = [new(1, 3)], ["2"] = [] } });
        var vm = new BusmasterViewModel(new DisplayLimits());
        vm.ApplySignalWatchOptions(new() { Signals = [A, B], OnlyWatched = true });
        vm.LoadDataset(dataset); vm.SetWatchCursorAt(2); await vm.WaitForWatchValuesAsync();
        var a = Assert.Single(vm.SignalWatchRows, row => row.Identity == A);
        var b = Assert.Single(vm.SignalWatchRows, row => row.Identity == B);
        Assert.Equal("1", a.PhysicalValue); Assert.Equal("0x64", a.RawValue);
        Assert.Contains("Meetgat", a.Status); Assert.DoesNotContain("Meetgat", b.Status);
        Assert.StartsWith("2", a.Age);
        var saved = JsonSerializer.Deserialize<SignalWatchOptions>(JsonSerializer.Serialize(vm.CaptureSignalWatchOptions()))!;
        Assert.Equal(new[] { A, B }, saved.Signals); Assert.True(saved.OnlyWatched);
        vm.SetWatchCursorAt(-1); await vm.WaitForWatchValuesAsync(); Assert.All(vm.SignalWatchRows, row => Assert.Equal("—", row.PhysicalValue));
        vm.WatchLatestValues = true; await vm.WaitForWatchValuesAsync(); Assert.Equal("9", a.PhysicalValue); Assert.Equal("—", a.Age);
        vm.ClearDataset(); Assert.Empty(vm.SignalWatchRows); Assert.Equal(2, vm.CaptureSignalWatchOptions().Signals.Count);
    }

    [Fact]
    public async Task FloatingProjectionIgnoresTableFiltersAndSharesLiveRowsAndSelection()
    {
        using var dataset = Dataset([Sample(0, 0, A, 1), Sample(0, 1, B, 2), Sample(4_000_000_000, 2, A, 9)],
            new ImportReport("test", 0, 0, 0, 0, []) { GapsByChannel = new Dictionary<string, IReadOnlyList<MeasurementGap>> { ["1"] = [new(1, 3)], ["2"] = [] } });
        var vm = new BusmasterViewModel(new DisplayLimits());
        try
        {
            vm.LoadDataset(dataset);
            Assert.Empty(vm.WatchedSignalRows);
            Assert.Contains("Geen signalen gekozen", vm.FloatingWatchStatus);
            var a = Assert.Single(vm.SignalWatchRows, row => row.Identity == A);
            var b = Assert.Single(vm.SignalWatchRows, row => row.Identity == B);
            a.IsWatched = true;
            vm.SetWatchCursorAt(2);
            await vm.WaitForWatchValuesAsync();
            Assert.Same(a, Assert.Single(vm.WatchedSignalRows));
            Assert.Equal("1", a.PhysicalValue);
            Assert.Contains("Meetgat", a.Status);
            Assert.Equal("1 signaal gevolgd.", vm.FloatingWatchStatus);

            var projection = vm.WatchedSignalRows;
            vm.SignalWatchSearchText = "no-such-signal";
            vm.OnlyWatchedSignals = true;
            vm.SearchText = "0x999";
            vm.ShowOnlyDecoded = true;
            vm.ApplyFiltersCommand.Execute(null);
            Assert.Empty(vm.SignalWatchRows);
            Assert.Same(projection, vm.WatchedSignalRows);
            Assert.Equal("1 signaal gevolgd.", vm.FloatingWatchStatus);
            vm.OnlyWatchedSignals = false;
            Assert.Same(projection, vm.WatchedSignalRows);

            var valueNotifications = 0;
            a.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(BusmasterSignalWatchRow.PhysicalValue)) valueNotifications++; };
            vm.WatchLatestValues = true;
            await vm.WaitForWatchValuesAsync();
            Assert.True(valueNotifications > 0);
            Assert.Same(a, Assert.Single(vm.WatchedSignalRows));
            Assert.Equal("9", vm.WatchedSignalRows[0].PhysicalValue);
            Assert.Equal(a.RawValue, vm.WatchedSignalRows[0].RawValue);
            Assert.Equal("Laatste sample in opname", vm.WatchedSignalRows[0].Status);

            // Unchecking the shared floating row changes the saved selection as well.
            vm.WatchedSignalRows[0].IsWatched = false;
            Assert.False(a.IsWatched);
            Assert.Empty(vm.WatchedSignalRows);
            Assert.Empty(vm.CaptureSignalWatchOptions().Signals);
            b.IsWatched = true;
            Assert.Same(b, Assert.Single(vm.WatchedSignalRows));
            Assert.Equal(B, Assert.Single(vm.CaptureSignalWatchOptions().Signals));
            Assert.Empty(vm.SignalWatchRows); // Main-table search still does not affect the floating view.
        }
        finally { await vm.CancelAndWaitAsync(); vm.ClearDataset(); }
    }

    [Fact]
    public async Task FloatingProjectionTracksOptionsDatasetChangesAndMissingSavedSignals()
    {
        var missing = A with { SignalName = "Missing" };
        using var first = Dataset([Sample(0, 0, A, 1), Sample(0, 1, B, 2)]);
        using var next = Dataset([Sample(0, 0, A, 3)]);
        using var empty = Dataset([]);
        var vm = new BusmasterViewModel(new DisplayLimits());
        try
        {
            Assert.Equal("Geen opname geladen.", vm.FloatingWatchStatus);
            vm.ApplySignalWatchOptions(new() { Signals = [A], LatestValues = true });
            Assert.Empty(vm.WatchedSignalRows);
            vm.LoadDataset(first);
            var firstA = Assert.Single(vm.WatchedSignalRows);
            var firstB = Assert.Single(vm.SignalWatchRows, row => row.Identity == B);
            Assert.Equal("1 signaal gevolgd.", vm.FloatingWatchStatus);

            vm.ApplySignalWatchOptions(new() { Signals = [B, missing], LatestValues = true, OnlyWatched = true });
            Assert.False(firstA.IsWatched);
            Assert.Same(firstB, Assert.Single(vm.WatchedSignalRows));
            Assert.Contains("1 gekozen signaal ontbreekt", vm.FloatingWatchStatus);
            var saved = JsonSerializer.Deserialize<SignalWatchOptions>(JsonSerializer.Serialize(vm.CaptureSignalWatchOptions()))!;
            Assert.Equal(new[] { B, missing }, saved.Signals);
            Assert.True(saved.LatestValues);
            Assert.True(saved.OnlyWatched);

            vm.LoadDataset(next);
            firstB.IsWatched = false; // Retired rows must no longer change the preserved selection.
            Assert.Empty(vm.WatchedSignalRows);
            Assert.Contains("0 signalen gevolgd", vm.FloatingWatchStatus);
            Assert.Contains("2 gekozen signalen ontbreken", vm.FloatingWatchStatus);
            Assert.Equal(saved.Signals, vm.CaptureSignalWatchOptions().Signals);
            vm.LoadDataset(empty);
            Assert.Empty(vm.WatchedSignalRows);
            Assert.Contains("2 gekozen signalen ontbreken", vm.FloatingWatchStatus);

            vm.ClearDataset();
            Assert.Empty(vm.WatchedSignalRows);
            Assert.Equal("Geen opname geladen.", vm.FloatingWatchStatus);
            Assert.Equal(saved.Signals, vm.CaptureSignalWatchOptions().Signals);
            await vm.CancelAndWaitAsync();
            Assert.Empty(vm.WatchedSignalRows); // A retired result must not repopulate the projection.

            vm.LoadDataset(first);
            await vm.WaitForWatchValuesAsync();
            Assert.Equal(B, Assert.Single(vm.WatchedSignalRows).Identity);
            Assert.Equal("2", vm.WatchedSignalRows[0].PhysicalValue);
            vm.ClearWatchedSignalsCommand.Execute(null);
            Assert.Empty(vm.WatchedSignalRows);
            Assert.Empty(vm.CaptureSignalWatchOptions().Signals);
            Assert.Contains("Geen signalen gekozen", vm.FloatingWatchStatus);
        }
        finally { await vm.CancelAndWaitAsync(); vm.ClearDataset(); }
    }

    private static CanDataset Dataset(IReadOnlyList<DecodedSignalSample> samples, ImportReport? report = null) => new()
    {
        RawFrames = [], DecodedSamples = samples, MessageSummaries = [], SignalLabels = [],
        SignalSeriesByLabel = new Dictionary<string, SignalSeries>(), SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries>(),
        Diagnostics = new(0, 0, 0, 0, 0, ""), ImportReport = report
    };

    [Fact]
    public async Task DatasetSwapDiscardsTheOldWatchResultAndKeepsItsStorageAliveUntilReadEnds()
    {
        var oldStore = new BlockingLookup(Sample(0, 0, A, 1));
        using var oldDataset = Dataset(oldStore);
        using var newDataset = Dataset([Sample(0, 0, A, 99)]);
        var vm = new BusmasterViewModel(new DisplayLimits());
        vm.ApplySignalWatchOptions(new() { Signals = [A], LatestValues = true, OnlyWatched = true });
        vm.LoadDataset(oldDataset);
        try
        {
            await oldStore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.LoadDataset(newDataset); oldDataset.Dispose();
            Assert.False(oldStore.Disposed);
            oldStore.Release.Set();
            await vm.WaitForWatchValuesAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(oldStore.Disposed);
            Assert.Equal("99", Assert.Single(vm.SignalWatchRows).PhysicalValue);
            await vm.CancelAndWaitAsync(); vm.ClearDataset();
            Assert.Empty(vm.SignalWatchRows);
        }
        finally { oldStore.Release.Set(); await vm.CancelAndWaitAsync(); oldStore.Release.Dispose(); }
    }

    [Fact]
    public async Task RetirementDetachesRowsImmediatelyButWaitsForTheActiveReaderBeforeDisposal()
    {
        var store = new BlockingLookup(Sample(0, 0, A, 1));
        using var dataset = Dataset(store);
        var vm = new BusmasterViewModel(new DisplayLimits());
        vm.ApplySignalWatchOptions(new() { Signals = [A], LatestValues = true, OnlyWatched = true });
        vm.LoadDataset(dataset);
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.ClearDataset();
            dataset.Dispose();
            Assert.Empty(vm.Messages);
            Assert.Empty(vm.SignalWatchRows);
            Assert.Null(vm.SelectedMessage);
            Assert.False(store.Disposed);

            var drained = vm.CancelAndWaitAsync();
            Assert.False(drained.IsCompleted);
            store.Release.Set();
            await drained.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(store.Disposed);
            Assert.Empty(vm.SignalWatchRows); // The retired result must not reappear.
            Assert.Throws<ObjectDisposedException>(() => dataset.AcquireReadLease());
        }
        finally
        {
            store.Release.Set();
            await vm.CancelAndWaitAsync();
            store.Release.Dispose();
        }
    }

    private sealed class BlockingLookup(DecodedSignalSample sample) : IReadOnlyList<DecodedSignalSample>, ISignalSampleLookup, IFrameSampleLookup, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool Disposed { get; private set; }
        public int Count => 1;
        public DecodedSignalSample this[int index] => index == 0 ? sample : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<DecodedSignalSample> GetEnumerator() { yield return sample; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IReadOnlyList<SignalSampleSummary> GetSignalSummaries() => [new(1, sample.Value, sample.Value, sample)];
        public IEnumerable<SignalSeriesPoint> ReadSignalSeries(SignalIdentity identity) => [new(sample.TimestampNanoseconds, sample.FrameIndex, sample.Value)];
        public SignalSeriesPoint? FindSignalPointAtOrBefore(SignalIdentity identity, long timestampNanoseconds)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Watch test was not released.");
            ObjectDisposedException.ThrowIf(Disposed, this);
            return new(sample.TimestampNanoseconds, sample.FrameIndex, sample.Value);
        }
        public bool TryGetFrameSummary(long frameIndex, out string messageName, out int sampleCount)
        { messageName = sample.MessageName; sampleCount = 1; return true; }
        public IReadOnlyList<DecodedSignalSample> GetFrameSamples(long frameIndex) => [sample];
        public void Dispose() => Disposed = true;
    }
}
