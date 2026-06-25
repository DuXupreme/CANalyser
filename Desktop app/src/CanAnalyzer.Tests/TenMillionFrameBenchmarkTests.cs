using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Storage;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class TenMillionFrameBenchmarkTests
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void TenMillionFrames_AppendFilterAndEnumerateWithoutTruncation()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CANALYSER_RUN_10M_BENCHMARK"), "1", StringComparison.Ordinal))
            return;

        const int count = 10_000_000;
        using var store = new DiskBackedFrameStore();
        for (var i = 0; i < count; i++)
        {
            store.Append(new RawCanFrame(i * 1_000L, (uint)(0x100 + (i % 32)), 8,
                [0, 1, 2, 3, 4, 5, 6, 7], "Rx", (i & 1).ToString(), false, i, i + 1));
        }

        store.Complete();
        Assert.Equal(count, store.Count);
        var filtered = new CanAnalyzer.Core.Analysis.RawFrameFilterService().Apply(
            store,
            new RawFrameFilterOptions { IdFilter = "0x100", MaxRows = count });
        Assert.Equal(count / 32, filtered.Count);
        Assert.Equal(count - 1, store[^1].FrameIndex);
    }
}
