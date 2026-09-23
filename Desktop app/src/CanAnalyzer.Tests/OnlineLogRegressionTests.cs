using System.Net;
using System.Text;
using System.Text.Json;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.Core.Storage;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class OnlineLogRegressionTests
{
    [Fact]
    public void SplitLogStartIncludesFirstRecordOffsetWithoutChangingTimeOrigin()
    {
        var origin = DateTimeOffset.Parse("2026-09-14T09:00:00Z");
        using var dataset = new CanAnalyzer.Core.Analysis.DatasetBuilder().Build(
            [new CanAnalyzer.Core.Domain.RawCanFrame(1724896000000, 0x123, 0, [], "Rx", "1", false)],
            [], [], new CanAnalyzer.Core.Domain.DecoderDiagnostics(0, 0, 0, 0, 0, ""), startTimeUtc: origin);
        Assert.Equal(origin, dataset.StartTimeUtc);
        Assert.Equal("2026-09-14 09:28:44.896000000 UTC",
            CanAnalyzer.Core.Utilities.MeasurementTimestamp.FormatUtc(origin, dataset.FirstRecordOffsetNanoseconds));
    }

    [Fact]
    public async Task DelayedUploadRetainsMorningRecordingTime()
    {
        using var service = Service(_ => Payload(false, new[] { Log("morning", "2026-09-14T09:28:44.896Z") }));
        var result = await service.GetLogsAsync("22484AAA", DateTimeOffset.Parse("2026-09-14T00:00:00Z"), DateTimeOffset.Parse("2026-09-15T00:00:00Z"), default);
        var log = Assert.Single(result.Files);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T09:28:44.896Z"), log.RecordedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T15:33:35Z"), log.UploadedAt);
    }

    [Fact]
    public async Task TruncatedDayFetchesBothHalvesAndPreservesSelectionLimit()
    {
        var calls = 0;
        using var service = Service(_ => ++calls switch
        {
            1 => Payload(true, new[] { Log("late", "2026-09-14T18:00:00Z") }),
            2 => Payload(false, new[] { Log("early", "2026-09-14T09:28:44Z") }),
            _ => Payload(false, new[] { Log("late", "2026-09-14T18:00:00Z") })
        });
        var result = await service.GetLogsAsync("22484AAA", DateTimeOffset.Parse("2026-09-14T00:00:00Z"), DateTimeOffset.Parse("2026-09-15T00:00:00Z"), default);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { "late", "early" }, result.Files.Select(file => file.Key));
        Assert.False(result.Truncated);
        Assert.Equal(CanAnalyzer.Core.Domain.Mdf4ImportLimits.MaximumFiles, result.MaximumSelection);
    }

    [Fact]
    public async Task MissingRecordingTimeNeverFallsBackToUpload()
    {
        using var service = Service(_ => Payload(false, new[] { Log("unknown", null) }));
        var result = await service.GetLogsAsync("22484AAA", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, default);
        Assert.Null(Assert.Single(result.Files).RecordedAt);
    }

    [Fact]
    public async Task LegacyUploadBasedApiIsRejected()
    {
        using var service = Service(_ => "{\"files\":[],\"timeBasis\":\"upload\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetLogsAsync("22484AAA", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public void ClearCachePreservesActiveLockedAndUnrelatedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "canalyser-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var active = Path.Combine(root, "online-logs-active.zip");
            var locked = Path.Combine(root, "online-logs-locked.zip.partial");
            File.WriteAllBytes(active, new byte[10]);
            File.WriteAllBytes(locked, new byte[20]);
            File.WriteAllBytes(Path.Combine(root, "online-logs-old.zip"), new byte[30]);
            File.WriteAllBytes(Path.Combine(root, "personal.zip"), new byte[40]);
            using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var cache = new OnlineDownloadCache(root);
            Assert.Equal(60, cache.Inspect().Bytes);
            var result = cache.Clear(active);
            Assert.Equal(30, result.DeletedBytes);
            Assert.Equal(30, result.Bytes);
            Assert.True(File.Exists(active));
            Assert.True(File.Exists(locked));
            Assert.True(File.Exists(Path.Combine(root, "personal.zip")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static object Log(string key, string? recordedAt) => new
    {
        key, name = key + ".MF4", machine = "Vlindermachine 2", logger = "22484AAA", session = "00000068",
        recordedAt, createdAt = "2026-09-14T15:33:35Z", uploadedAt = "2026-09-14T15:33:35Z", sizeBytes = 100
    };

    private static string Payload(bool truncated, object[] files) => JsonSerializer.Serialize(new
    { files, truncated, maximumSelection = 200, timeBasis = "recording", unknownRecordingTimes = 0 });

    private static OnlineLogService Service(Func<HttpRequestMessage, string> respond) => new(
        new HttpClient(new Handler(respond)) { BaseAddress = new Uri("https://example.test/") });

    private sealed class Handler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("timeBasis=recording", request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
        }
    }
}
