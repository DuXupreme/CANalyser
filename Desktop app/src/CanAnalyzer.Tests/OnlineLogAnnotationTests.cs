using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class OnlineLogAnnotationTests
{
    [Fact]
    public void OnlyExplicitlyMarkedIntermediatePartsFillSelectionHoles()
    {
        var first = Selection(1); var middle = Selection(2, true); var last = Selection(3);
        var selected = OnlineLogSelectionPolicy.IncludeMarkedBetweenSelections([first, last], [first, middle, last, Selection(4, true)]);
        Assert.Equal(3, selected.Length);
        Assert.Contains(middle, selected);
        Assert.Equal(2, OnlineLogSelectionPolicy.IncludeMarkedBetweenSelections([first, last], [Selection(2)]).Length);
        Assert.Equal(2, OnlineLogSelectionPolicy.IncludeMarkedBetweenSelections([first, last], [middle with { Session = "00000073" }]).Length);
    }

    [Fact]
    public async Task OmittedMiddlePartIsManifestedAndCacheRechecksSharedRevision()
    {
        using var fixture = new Fixture();
        var path = await fixture.Service.DownloadArchiveAsync(fixture.Files, null, default);
        using (var zip = ZipFile.OpenRead(path))
        {
            using var stream = zip.GetEntry(OnlineLogArchiveManifestCodec.EntryName)!.Open();
            var manifest = await OnlineLogArchiveManifestCodec.ReadAsync(stream);
            Assert.Equal(2, manifest.SchemaVersion);
            Assert.Equal(2, manifest.Files.Count);
            Assert.Equal("Niet bruikbaar", Assert.Single(manifest.OmittedFiles).Reason);
            Assert.Null(zip.GetEntry(fixture.Files[1].Key));
        }
        var calls = fixture.PlanCalls;
        Assert.Equal(path, await fixture.Service.DownloadArchiveAsync(fixture.Files, null, default));
        Assert.True(fixture.PlanCalls > calls);
        Assert.Equal(2, fixture.Downloads);
        fixture.Conflict = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.DownloadArchiveAsync(fixture.Files, null, default));
        Assert.Equal(2, fixture.Downloads);
    }

    [Fact]
    public async Task UnknownSharedAnnotationCannotReuseCacheOrDownloadAsUnmarked()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadArchiveAsync(
            [Selection(1) with { Annotation = null, RequiresAnnotationCheck = true }], null, default));
        Assert.Equal(0, fixture.Downloads);
    }

    [Fact]
    public async Task CacheCannotHideAChangedMetadataRevisionInTheFreshPlan()
    {
        using var fixture = new Fixture();
        await fixture.Service.DownloadArchiveAsync(fixture.Files, null, default);
        fixture.MetadataRevision = "metadata-v2";
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadArchiveAsync(fixture.Files, null, default));
        Assert.Equal(2, fixture.Downloads);
    }

    [Fact]
    public async Task ConcurrentMarkDuringDownloadCannotPublishTheArchive()
    {
        using var fixture = new Fixture { ChangeAfterDownload = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.DownloadArchiveAsync(fixture.Files, null, default));
        Assert.Equal(2, fixture.Downloads);
        Assert.Empty(Directory.GetFiles(fixture.CacheDirectory));
    }

    [Fact]
    public async Task ReceiptSearchPreservesUnknownAnnotationState()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Contains("timeBasis=uploaded", request.RequestUri!.Query);
            Assert.Contains("includeExcluded=true", request.RequestUri.Query);
            await Task.Yield();
            return Json(new { timeBasis = "uploaded", files = new[] { new {
                key = "22484AAA/00000072/00000001.MF4", name = "00000001.MF4", machine = "M2", logger = "22484AAA", session = "00000072",
                sizeBytes = 3, uploadedAt = "2026-10-28T10:00:00Z", objectVersion = "s3rev1:x", annotation = new { excluded = false, reason = "" }
            } }, capabilities = new { annotations = new { available = false, version = 1, shared = true }, downloadManifestVersion = 2 } });
        });
        using var service = new OnlineLogService(new HttpClient(handler) { BaseAddress = new("https://dashboard.example.test/") });
        var result = await service.GetLogsAsync("22484AAA", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "uploaded", default);
        Assert.False(result.AnnotationsAvailable);
        var file = Assert.Single(result.Files);
        Assert.Null(file.Annotation);
        Assert.Null(file.RecordedAt);
        Assert.True(file.ToSelection().RequiresAnnotationCheck);
    }

    [Fact]
    public async Task AnnotationMutationSendsBothExpectedRevisionsAndRequiresReason()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("/api/log-annotations", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var update = body.RootElement.GetProperty("updates")[0];
            Assert.Equal("object-v1", update.GetProperty("expectedObjectVersion").GetString());
            Assert.Equal("annotation-v1", update.GetProperty("expectedAnnotationRevision").GetString());
            return Json(new { annotations = new[] { new {
                key = "a", objectVersion = "object-v1", revision = "annotation-v2", excluded = true,
                reason = "testopname", actor = "shared-dashboard-access"
            } } });
        });
        using var service = new OnlineLogService(new HttpClient(handler) { BaseAddress = new("https://dashboard.example.test/") });
        Assert.Single(await service.UpdateAnnotationsAsync([new("a", "object-v1", "annotation-v1", true, "testopname")], default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAnnotationsAsync([new("a", "object-v1", null, true, "")], default));
    }

    private static OnlineLogSelection Selection(int part, bool excluded = false) => new(
        $"22484AAA/00000072/{part:D8}.MF4", $"{part:D8}.MF4", "22484AAA", "00000072", 3)
    {
        ObjectVersion = "s3rev1:" + part, ETag = "\"etag" + part + "\"", RequiresAnnotationCheck = true, MetadataRevision = "metadata-v1",
        UploadedAtUtc = DateTimeOffset.Parse("2026-10-28T10:00:00Z"),
        Annotation = new(excluded, excluded ? "Niet bruikbaar" : "", excluded ? "revision1" : null)
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "canalyser-annotations-" + Guid.NewGuid().ToString("N"));
        public string CacheDirectory => _directory;
        public OnlineLogSelection[] Files { get; } = [Selection(1), Selection(2, true), Selection(3)];
        public OnlineLogService Service { get; }
        public int PlanCalls { get; private set; }
        public int Downloads { get; private set; }
        public bool Conflict { get; set; }
        public bool ChangeAfterDownload { get; set; }
        public string MetadataRevision { get; set; } = "metadata-v1";
        public Fixture()
        {
            Service = new(new HttpClient(new Handler(async request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    PlanCalls++;
                    if (Conflict || ChangeAfterDownload && Downloads == 2) return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { error = "annotation_conflict" }) };
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    Assert.False(body.RootElement.GetProperty("includeExcluded").GetBoolean());
                    Assert.True(body.RootElement.GetProperty("expectedAnnotations").GetArrayLength() > 0);
                    var keys = body.RootElement.GetProperty("keys").EnumerateArray().Select(item => item.GetString()).ToArray();
                    var batch = Files.Where(file => keys.Contains(file.Key)).ToArray();
                    return Json(new { schemaVersion = 2, files = batch.Where(file => !file.Annotation!.Excluded).Select(file => new {
                        key = file.Key, archiveName = file.Key, url = "https://storage.example.test/" + file.Name,
                        sizeBytes = file.SizeBytes, objectVersion = file.ObjectVersion, eTag = file.ETag, annotationRevision = file.Annotation!.Revision,
                        metadataRevision = MetadataRevision
                    }), omittedFiles = batch.Where(file => file.Annotation!.Excluded).Select(file => new OnlineLogArchiveOmission {
                        ArchiveName = file.Key, ObjectKey = file.Key, LoggerId = file.Logger, Session = file.Session,
                        ObjectVersion = file.ObjectVersion!, SizeBytes = file.SizeBytes, Reason = file.Annotation!.Reason,
                        AnnotationRevision = file.Annotation.Revision!
                    }) });
                }
                Downloads++;
                var selected = Files.Single(file => request.RequestUri!.AbsolutePath.EndsWith(file.Name));
                Assert.Equal(selected.ETag, Assert.Single(request.Headers.IfMatch).ToString());
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                response.Headers.ETag = EntityTagHeaderValue.Parse(selected.ETag!);
                response.Content.Headers.LastModified = selected.UploadedAtUtc;
                return response;
            })) { BaseAddress = new("https://dashboard.example.test/") }, _directory);
        }
        public void Dispose()
        {
            Service.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
