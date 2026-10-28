using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Services;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class OnlineLogObjectRevisionTests
{
    [Theory]
    [InlineData("etag")]
    [InlineData("version")]
    [InlineData("missing-etag")]
    [InlineData("missing-version")]
    public async Task SameSizedGetMustMatchThePlannedObjectRevision(string change)
    {
        using var fixture = new RevisionFixture(change);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.DownloadArchiveAsync([fixture.Selection], null, default));
        Assert.Equal(1, fixture.Downloads);
        Assert.Empty(Directory.GetFiles(fixture.CacheDirectory));
    }

    [Fact]
    public async Task MatchingObjectRevisionPublishesTheDownload()
    {
        using var fixture = new RevisionFixture("unchanged");
        var path = await fixture.Service.DownloadArchiveAsync([fixture.Selection], null, default);
        Assert.True(File.Exists(path));
        Assert.Equal(1, fixture.Downloads);
    }

    private sealed class RevisionFixture : IDisposable
    {
        public string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "canalyser-revision-test-" + Guid.NewGuid().ToString("N"));
        public OnlineLogSelection Selection { get; } = new("22484AAA/00000072/00000001.MF4", "00000001.MF4", "22484AAA", "00000072", 3)
        {
            ObjectVersion = "version-v1", ETag = "\"etag-v1\"", MetadataRevision = "metadata-v1",
            UploadedAtUtc = DateTimeOffset.Parse("2026-10-19T10:00:00Z")
        };
        public OnlineLogService Service { get; }
        public int Downloads { get; private set; }

        public RevisionFixture(string change)
        {
            Service = new OnlineLogService(new HttpClient(new Handler(request =>
            {
                if (request.Method == HttpMethod.Post)
                    return new(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            files = new[] { new
                            {
                                key = Selection.Key, archiveName = Selection.Key,
                                url = "https://storage.example.test/data", sizeBytes = 3,
                                objectVersion = "version-v1", versionId = "version-v1", eTag = "\"etag-v1\"",
                                metadataRevision = "metadata-v1", uploadedAt = Selection.UploadedAtUtc
                            } }
                        })
                    };

                Downloads++;
                Assert.Equal("\"etag-v1\"", Assert.Single(request.Headers.IfMatch).ToString());
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                response.Content.Headers.LastModified = Selection.UploadedAtUtc;
                if (change != "missing-etag")
                    response.Headers.ETag = new EntityTagHeaderValue(change == "etag" ? "\"etag-v2\"" : "\"etag-v1\"");
                if (change != "missing-version")
                    response.Headers.Add("x-amz-version-id", change == "version" ? "version-v2" : "version-v1");
                return response;
            })) { BaseAddress = new Uri("https://dashboard.example.test/") }, CacheDirectory);
        }

        public void Dispose()
        {
            Service.Dispose();
            if (Directory.Exists(CacheDirectory)) Directory.Delete(CacheDirectory, true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
