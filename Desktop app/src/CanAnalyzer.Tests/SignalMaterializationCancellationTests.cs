using System.Collections;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Utilities;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class SignalMaterializationCancellationTests
{
    private static readonly SignalIdentity Identity = new("1", CanFrameFormat.Classic, false, 1, "synthetic", "value");

    [Fact]
    public void CancelledLoaderResult_IsNotCached_AndRetryReturnsCompleteSource()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var series = new SignalSeries(Identity, token =>
        {
            calls++;
            if (calls == 1) cancellation.Cancel();
            return (new long[] { 0, 1_000_000_000 }, new double[] { 17, 23 });
        });
        Assert.Throws<OperationCanceledException>(() => series.GetData(cancellation.Token));
        Assert.False(series.IsMaterialized);
        var result = series.GetData();
        Assert.Equal(new[] { 0d, 1 }, result.Time);
        Assert.Equal(new[] { 17d, 23 }, result.Value);
        Assert.Equal(2, calls);
        Assert.Same(result.Time, series.Time);
        Assert.Same(result.Value, series.Value);
    }

    [Fact]
    public async Task WaitingReader_CanCancelWithoutCancellingOrPublishingTheCurrentLoader()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var series = new SignalSeries(Identity, token =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return (new long[] { 1, 2, 3 }, new double[] { 4, 5, 6 });
        });
        var first = Task.Run(() => series.GetData());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiting = Task.Run(() =>
            {
                waiterEntered.SetResult();
                return series.GetData(cancellation.Token);
            });
            await waiterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(series.IsMaterialized);
            Assert.False(first.IsCompleted);
        }
        finally { release.Set(); }
        var complete = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal(new[] { 4d, 5, 6 }, complete.Value);
        Assert.Same(complete.Time, series.GetData().Time);
    }

    [Fact]
    public void DatasetLazyRead_CancelsAndDisposesEnumerator_ThenRetriesWithExactTimestampFrameOrder()
    {
        using var cancellation = new CancellationTokenSource();
        var samples = new CancellingSamples(cancellation);
        using var dataset = new DatasetBuilder().Build([], samples, [], new(0, 0, 1, 0, 0, ""));
        var series = dataset.SignalSeriesByIdentity[Identity];

        Assert.Throws<OperationCanceledException>(() => series.GetData(cancellation.Token));
        Assert.InRange(samples.Visited, 8, 4097);
        Assert.True(samples.EnumeratorDisposed);
        Assert.False(series.IsMaterialized);
        var actual = series.GetData();
        var expected = Enumerable.Range(0, samples.Count).Select(i => samples[i])
            .OrderBy(s => s.TimestampNanoseconds).ThenBy(s => s.FrameIndex).ToArray();
        Assert.Equal(expected.Select(s => s.TimestampNanoseconds), series.TimestampNanoseconds);
        Assert.Equal(expected.Select(s => s.Value), actual.Value);
        Assert.Equal(expected.Select(s => s.TimeSeconds), actual.Time);
        Assert.Equal(samples.Count, samples.Visited);
        Assert.True(samples.EnumeratorDisposed);
        Assert.Equal(0, samples[0].FrameIndex);
        Assert.Equal(samples.Count - 1, samples[^1].FrameIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(33)]
    [InlineData(10_003)]
    public void CancellableSort_PreservesStableTieOrder(int count)
    {
        var source = Enumerable.Range(0, count).Select(i => (Key: (count - i) % 17, Position: i)).ToArray();
        var expected = source.OrderBy(item => item.Key).ToArray();
        CancellableArrayOperations.Sort(source, (left, right) => left.Key.CompareTo(right.Key), CancellationToken.None);
        Assert.Equal(expected, source);
    }

    [Fact]
    public void CancellationDuringSort_RemainsOperationCanceledException_AndStopsWithinOneChunk()
    {
        using var cancellation = new CancellationTokenSource();
        var data = Enumerable.Range(0, 50_000).Reverse().ToArray();
        var comparisons = 0;
        Assert.Throws<OperationCanceledException>(() => CancellableArrayOperations.Sort(data, (left, right) =>
        {
            if (++comparisons == 8) cancellation.Cancel();
            return left.CompareTo(right);
        }, cancellation.Token));
        Assert.InRange(comparisons, 8, 4104);
    }

    [Fact]
    public void AlreadyCancelledMaterializedReadsAndCopies_DoNotReturnOrMutateBuffers()
    {
        var series = new SignalSeries("test", [0, 1], [2, 3]);
        var token = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => series.GetTime(token));
        Assert.Throws<OperationCanceledException>(() => series.GetValues(token));
        Assert.Throws<OperationCanceledException>(() => series.GetTimestampNanoseconds(token));
        Assert.Throws<OperationCanceledException>(() => CancellableArrayOperations.ToArray(new List<int> { 1, 2 }, token));
        var target = new List<int> { 9 };
        Assert.Throws<OperationCanceledException>(() => CancellableArrayOperations.AddRange(target, [1, 2], token));
        Assert.Equal(new[] { 9 }, target);
        Assert.Equal(new[] { 2d, 3 }, series.Value);
    }

    private sealed class CancellingSamples(CancellationTokenSource cancellation) : IReadOnlyList<DecodedSignalSample>, ISignalSampleLookup
    {
        public int Count => 10_003;
        public int Visited { get; private set; }
        public bool EnumeratorDisposed { get; private set; }
        public DecodedSignalSample this[int index] => new((Count - index / 2L) * 1_000_000_000L,
            index, index, Identity, index, index);
        public IReadOnlyList<SignalSampleSummary> GetSignalSummaries() => [new(Count, 0, Count - 1, this[Count - 1])];
        public IEnumerable<SignalSeriesPoint> ReadSignalSeries(SignalIdentity identity)
        {
            Visited = 0; EnumeratorDisposed = false;
            try
            {
                for (var i = 0; i < Count; i++)
                {
                    if (++Visited == 8) cancellation.Cancel();
                    var sample = this[i];
                    yield return new(sample.TimestampNanoseconds, sample.FrameIndex, sample.Value);
                }
            }
            finally { EnumeratorDisposed = true; }
        }
        public IEnumerator<DecodedSignalSample> GetEnumerator() => Enumerable.Range(0, Count).Select(i => this[i]).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
