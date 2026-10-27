using CanAnalyzer.Core.Analysis;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class SignalRangeIndexTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(1000)]
    public void StableSelection_PreservesSourcePeaksEndpointsAndBudget(int budget)
    {
        var x = Enumerable.Range(0, 10_003).Select(i => (double)i).ToArray();
        var y = x.Select(t => Math.Sin(t * 0.17)).ToArray();
        y[1234] = -100; y[4321] = 200;
        var original = y.ToArray();
        var index = new SignalRangeIndex(x, y);

        var result = index.SelectStable(null, null, budget);

        Assert.InRange(result.X.Length, 2, budget);
        Assert.Equal(x[0], result.X[0]);
        Assert.Equal(x[^1], result.X[^1]);
        Assert.Contains(-100d, result.Y); Assert.Contains(200d, result.Y);
        Assert.Equal(result.X.Distinct().Order(), result.X);
        for (var i = 0; i < result.X.Length; i++) Assert.Equal(y[(int)result.X[i]], result.Y[i]);
        Assert.Equal(original, y);
    }

    [Fact]
    public void Panning_DoesNotReselectExtremaInSharedInteriorBuckets()
    {
        var x = Enumerable.Range(0, 50_000).Select(i => (double)i).ToArray();
        var y = x.Select(t => Math.Sin(t * 0.173) + Math.Cos(t * 0.091)).ToArray();
        var index = new SignalRangeIndex(x, y);
        var first = index.SelectStable(10_000, 30_000, 200);

        for (var pan = 1; pan <= 97; pan += 16)
        {
            var next = index.SelectStable(10_000 + pan, 30_000 + pan, 200);
            // These samples lie well inside both views, clear of their partial edge buckets.
            var common = first.X.Where(t => t >= 11_000 && t <= 29_000).ToArray();
            Assert.NotEmpty(common);
            Assert.Equal(common, next.X.Where(t => t >= 11_000 && t <= 29_000));
            Assert.InRange(next.X.Length, 2, 200);
        }
    }

    [Fact]
    public void StableSelection_ExactZoomKeepsDuplicateTimestampsAndBoundaryNeighbors()
    {
        double[] x = [0, 1, 1, 1, 2, 3];
        double[] y = [10, -2, 8, 5, 20, 30];
        var index = new SignalRangeIndex(x, y);

        var result = index.SelectStable(1, 1, 200);

        Assert.Equal(x[..5], result.X);
        Assert.Equal(y[..5], result.Y);
        Assert.Equal(index.Select(1, 1, int.MaxValue).X, result.X);
        Assert.Empty(index.SelectStable(5, 6, 200).X);
        var crossing = new SignalRangeIndex([0, 10], [2, 3]).SelectStable(4, 6, 200);
        Assert.Equal(new[] { 0d, 10 }, crossing.X);
    }

    [Fact]
    public void Selection_RejectsCancellationWithoutChangingPublishedSource()
    {
        double[] x = [0, 1, 2, 3];
        double[] y = [10, 20, 30, 40];
        var index = new SignalRangeIndex(x, y);
        var cancelled = new CancellationToken(true);

        Assert.Throws<OperationCanceledException>(() => new SignalRangeIndex(x, y, cancelled));
        Assert.Throws<OperationCanceledException>(() => index.Select(0, 2, 200, cancelled));
        Assert.Throws<OperationCanceledException>(() => index.SelectStable(0, 2, 200, cancelled));
        var result = index.Select(null, null, int.MaxValue);
        Assert.Same(x, result.X); Assert.Same(y, result.Y);
        Assert.Equal(new[] { 10d, 20, 30, 40 }, result.Y);
    }
}
