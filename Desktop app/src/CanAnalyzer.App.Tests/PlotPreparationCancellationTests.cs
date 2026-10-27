using System.Collections;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using OxyPlot.Annotations;
using OxyPlot.Series;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class PlotPreparationCancellationTests
{
    [Fact]
    public async Task BuilderPassesCancellationIntoLazyLoad_AndRetryPublishesOnlyCompleteSource()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var identity = new SignalIdentity("1", CanFrameFormat.Classic, false, 1, "synthetic", "value");
        var source = new SignalSeries(identity, token =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Assert.True(token.CanBeCanceled);
                started.TrySetResult();
                Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
                token.ThrowIfCancellationRequested();
            }
            return (new long[] { 0, 1_000_000_000, 2_000_000_000 }, new double[] { 10, -4, 17 });
        });
        using var dataset = Dataset(source);
        var builder = new PlotModelBuilder();
        PlotGroup[] groups = [new() { Signals = [source.Label] }];
        var options = new PlotViewOptions { UseDownsampling = true };
        var build = Task.Run(() => builder.Build(dataset, groups, options, null, cancellation.Token));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await build.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(source.IsMaterialized);

        var panel = Assert.Single(builder.Build(dataset, groups, options));
        var rendered = Assert.Single(panel.SeriesData);
        var line = Assert.IsAssignableFrom<LineSeries>(Assert.Single(panel.PlotModel.Series));
        Assert.Equal(2, calls);
        Assert.Same(source.Time, rendered.Time);
        Assert.Same(source.Value, rendered.Value);
        Assert.Equal(new[] { 0d, 1, 2 }, line.Points.Select(p => p.X));
        Assert.Equal(new[] { 10d, -4, 17 }, line.Points.Select(p => p.Y));
    }

    [Fact]
    public void GapAnnotationEnumeration_CanCancelAfterRendererIndexing_AndRetryPreservesGapOrder()
    {
        using var cancellation = new CancellationTokenSource();
        var gaps = new CancellingAnnotationGaps(cancellation);
        var source = new SignalSeries("test", [0, 1, 2], [8, 3, 5]);
        using var dataset = Dataset(source, new("synthetic", 0, 0, 0, 0, []) { Gaps = gaps });
        var builder = new PlotModelBuilder();
        PlotGroup[] groups = [new() { Signals = [source.Label] }];
        var options = new PlotViewOptions { UseDownsampling = true };

        Assert.Throws<OperationCanceledException>(() => builder.Build(dataset, groups, options, null, cancellation.Token));
        Assert.Equal(2, gaps.Enumerations);
        Assert.InRange(gaps.Visited, 8, 4097);
        Assert.True(gaps.Disposed);

        var panel = Assert.Single(builder.Build(dataset, groups, options));
        var annotations = panel.PlotModel.Annotations.OfType<RectangleAnnotation>().ToArray();
        Assert.Equal(gaps.Count, annotations.Length);
        Assert.Equal(10_000, annotations[0].MinimumX);
        Assert.Equal(10_000 + (gaps.Count - 1) * 4, annotations[^1].MinimumX);
        Assert.All(annotations, annotation => Assert.Equal(2, annotation.MaximumX - annotation.MinimumX));
        Assert.Equal(new[] { 8d, 3, 5 }, source.Value);
        Assert.Equal(new[] { 0d, 1, 2 }, source.Time);
    }

    private static CanDataset Dataset(SignalSeries source, ImportReport? report = null) => new()
    {
        RawFrames = [], DecodedSamples = [], MessageSummaries = [],
        SignalLabels = [source.Label],
        SignalSeriesByLabel = new Dictionary<string, SignalSeries> { [source.Label] = source },
        SignalSeriesByIdentity = new Dictionary<SignalIdentity, SignalSeries> { [source.Identity] = source },
        Diagnostics = new(0, 0, 1, 0, 0, ""), ImportReport = report
    };

    private sealed class CancellingAnnotationGaps(CancellationTokenSource cancellation) : IReadOnlyList<MeasurementGap>
    {
        public int Count => 5_000;
        public int Enumerations { get; private set; }
        public int Visited { get; private set; }
        public bool Disposed { get; private set; }
        public MeasurementGap this[int index] => new(10_000 + index * 4, 10_002 + index * 4);
        public IEnumerator<MeasurementGap> GetEnumerator()
        {
            Enumerations++; Visited = 0; Disposed = false;
            try
            {
                for (var i = Count - 1; i >= 0; i--)
                {
                    if (++Visited == 8 && Enumerations == 2) cancellation.Cancel();
                    yield return this[i];
                }
            }
            finally { Disposed = true; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
