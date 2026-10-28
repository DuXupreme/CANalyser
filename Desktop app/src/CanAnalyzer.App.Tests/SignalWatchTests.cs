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
