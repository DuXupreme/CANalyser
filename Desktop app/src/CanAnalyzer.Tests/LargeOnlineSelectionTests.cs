using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Parsing;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class LargeOnlineSelectionTests
{
    [Fact]
    public async Task FullDayDownloadsAcrossApiBatchesAndImportsAll371Files()
    {
        var root = Path.Combine(Path.GetTempPath(), "canalyser-full-day-test-" + Guid.NewGuid().ToString("N"));
        using var handler = new BatchHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        using var service = new OnlineLogService(client, root);
        try
        {
            var path = await service.DownloadArchiveAsync(Selection(371), null, default);
            Assert.Equal(new[] { 200, 171 }, handler.BatchSizes);
            Assert.Equal(371, handler.Downloads);
            using (var zip = ZipFile.OpenRead(path)) Assert.Equal(371, zip.Entries.Count);
            var converter = new CompleteSelectionConverter();
            var parser = new Mdf4Parser(converter, new PeakTrcParser());
            var parsed = await parser.ParseAsync(path, ImportMode.Strict, null, default);
            Assert.NotNull(parsed);
            try
            {
                Assert.Equal(371, converter.ImportedCount);
                Assert.Equal(371, parsed.Frames.Count);
                Assert.Equal(DatasetCompleteness.Complete, parsed.Completeness);
            }
            finally { (parsed.Frames as IDisposable)?.Dispose(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedSecondBatchLeavesNoPartialOrApparentlyCompleteArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "canalyser-failed-batch-test-" + Guid.NewGuid().ToString("N"));
        using var handler = new BatchHandler { FailSecondBatch = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        using var service = new OnlineLogService(client, root);
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadArchiveAsync(Selection(371), null, default));
            Assert.Equal(200, handler.Downloads);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static OnlineLogSelection[] Selection(int count) => Enumerable.Range(1, count)
        .Select(i => new OnlineLogSelection($"22484AAA/00000072/{i:D8}.MF4", $"{i:D8}.MF4", "22484AAA", "00000072", 1)).ToArray();

    private sealed class BatchHandler : HttpMessageHandler
    {
        public List<int> BatchSizes { get; } = [];
        public int Downloads { get; private set; }
        public bool FailSecondBatch { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadFromJsonAsync<PlanRequest>(cancellationToken);
                Assert.NotNull(body);
                Assert.InRange(body.Keys.Length, 1, 200);
                BatchSizes.Add(body.Keys.Length);
                if (FailSecondBatch && BatchSizes.Count == 2) return new(HttpStatusCode.ServiceUnavailable);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    files = body.Keys.Select(key => new { archiveName = key, url = "https://example.test/data/" + key }),
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1)
                }) };
            }
            Downloads++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([42]) };
        }
    }

    private sealed record PlanRequest(string[] Keys);

    private sealed class CompleteSelectionConverter : IMdf4ConversionService
    {
        public int ImportedCount { get; private set; }
        public async Task<IReadOnlyList<string>> ConvertToPeakTrcAsync(IReadOnlyList<string> inputPaths,
            string outputDirectory, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
        {
            ImportedCount = inputPaths.Count;
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, "converted.trc");
            var lines = new List<string> { ";$FILEVERSION=1.1", ";$STARTTIME=46279" };
            for (var i = 0; i < inputPaths.Count; i++)
            {
                Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(inputPaths[i], cancellationToken));
                lines.Add($"{i + 1}) {(i * 1000).ToString(CultureInfo.InvariantCulture)}.000 Rx 123 1 2A");
            }
            await File.WriteAllLinesAsync(path, lines, cancellationToken);
            return [path];
        }
    }
}
