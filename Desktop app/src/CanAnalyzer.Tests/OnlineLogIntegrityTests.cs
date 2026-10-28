using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class OnlineLogIntegrityTests
{
    [Fact]
    public async Task DownloadPreservesRawClockAndUploadAsSeparateUnverifiedEvidence()
    {
        using var fixture = new DownloadFixture();
        var selected = fixture.Selection;
        var path = await fixture.Service.DownloadArchiveAsync([selected], null, default);
        using var zip = ZipFile.OpenRead(path);
        await using var stream = zip.GetEntry(OnlineLogArchiveManifestCodec.EntryName)!.Open();
        var file = Assert.Single((await OnlineLogArchiveManifestCodec.ReadAsync(stream)).Files);
        Assert.Equal(selected.RawRecordedAtUtc, file.RawRecordedAtUtc);
        Assert.Equal(selected.UploadedAtUtc, file.UploadedAtUtc);
        Assert.Equal("unverified", file.ClockQuality);
        Assert.Equal("unverified", file.MachineAssignmentQuality);
        Assert.Equal(selected.Key, file.ObjectKey);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3])), file.Sha256);
        await using var data = zip.GetEntry(file.ArchiveName)!.Open();
        await OnlineLogArchiveManifestCodec.ValidateFileAsync(data, file);
    }

    [Fact]
    public async Task CacheReuseHashesEveryEntryAndRejectsSameLengthCorruption()
    {
        using var fixture = new DownloadFixture();
        var path = await fixture.Service.DownloadArchiveAsync([fixture.Selection], null, default);
        Assert.Equal(path, await fixture.Service.DownloadArchiveAsync([fixture.Selection], null, default));
        Assert.Equal(1, fixture.Downloads);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry(fixture.Selection.Key)!.Delete();
            await using var stream = zip.CreateEntry(fixture.Selection.Key).Open();
            await stream.WriteAsync(new byte[] { 3, 2, 1 });
        }
        await fixture.Service.DownloadArchiveAsync([fixture.Selection], null, default);
        Assert.Equal(2, fixture.Downloads);
    }

    [Fact]
    public async Task SameSizedReplacementOrMetadataRevisionCannotReuseOldCache()
    {
        using var fixture = new DownloadFixture();
        var first = await fixture.Service.DownloadArchiveAsync([fixture.Selection], null, default);
        var corrected = fixture.Selection with { MetadataRevision = "2", ClockQuality = "suspect" };
        var second = await fixture.Service.DownloadArchiveAsync([corrected], null, default);
        var replaced = corrected with { ObjectVersion = "new-object", UploadedAtUtc = corrected.UploadedAtUtc!.Value.AddMinutes(1) };
        var third = await fixture.Service.DownloadArchiveAsync([replaced], null, default);
        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
        Assert.Equal(3, fixture.Downloads);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public async Task TruncatedAndOversizedObjectsNeverPublishAnArchive(int bytes, bool advertiseExpectedLength)
    {
        using var fixture = new DownloadFixture { Bytes = new byte[bytes], AdvertiseExpectedLength = advertiseExpectedLength };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadArchiveAsync([fixture.Selection], null, default));
        Assert.Empty(Directory.GetFiles(fixture.CacheDirectory));
    }

    [Fact]
    public async Task ExpectedChecksumMismatchNeverPublishesAnArchive()
    {
        using var fixture = new DownloadFixture();
        var selection = fixture.Selection with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadArchiveAsync([selection], null, default));
        Assert.Empty(Directory.GetFiles(fixture.CacheDirectory));
    }

    [Fact]
    public async Task UnexpectedPlanKeyCannotBeAssignedToSelectedLog()
    {
        using var fixture = new DownloadFixture { PlanKey = "22484AAA/00000072/00000099.MF4" };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadArchiveAsync([fixture.Selection], null, default));
        Assert.Equal(0, fixture.Downloads);
        Assert.Empty(Directory.GetFiles(fixture.CacheDirectory));
    }

    [Fact]
    public async Task DuplicatePlanEntriesAreRejectedEvenWhenCountsMatch()
    {
        using var fixture = new DownloadFixture { DuplicatePlan = true };
        var second = fixture.Selection with { Key = "22484AAA/00000072/00000002.MF4", Name = "00000002.MF4" };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadArchiveAsync([fixture.Selection, second], null, default));
        Assert.Empty(Directory.GetFiles(fixture.CacheDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnversionedLegacySelectionDoesNotReuseAnUnverifiableCache(bool hasUploadTime)
    {
        using var fixture = new DownloadFixture();
        var legacy = fixture.Selection with
        { UploadedAtUtc = hasUploadTime ? fixture.Selection.UploadedAtUtc : null, ObjectVersion = null, ETag = null, Sha256 = null };
        await fixture.Service.DownloadArchiveAsync([legacy], null, default);
        await fixture.Service.DownloadArchiveAsync([legacy], null, default);
        Assert.Equal(2, fixture.Downloads);
    }

    [Theory]
    [InlineData("../escape.MF4")]
    [InlineData("/absolute.MF4")]
    [InlineData("A\\unsafe.MF4")]
    [InlineData("A/./unsafe.MF4")]
    [InlineData("A/file.MF4:stream")]
    public void ManifestRejectsUnsafeNames(string name)
    {
        Assert.Throws<InvalidDataException>(() => OnlineLogArchiveManifestCodec.ValidateManifest(Manifest(FileMetadata() with { ArchiveName = name })));
    }

    [Fact]
    public void ManifestRejectsUnsupportedVersionDuplicatesAndUnprovenVerifiedClaims()
    {
        var file = FileMetadata();
        Assert.Throws<InvalidDataException>(() => OnlineLogArchiveManifestCodec.ValidateManifest(Manifest(file) with { SchemaVersion = 99 }));
        Assert.Throws<InvalidDataException>(() => OnlineLogArchiveManifestCodec.ValidateManifest(new() { Files = [file, file] }));
        Assert.Throws<InvalidDataException>(() => OnlineLogArchiveManifestCodec.ValidateManifest(Manifest(file with { ClockQuality = "verified" })));
        Assert.Throws<InvalidDataException>(() => OnlineLogArchiveManifestCodec.ValidateManifest(Manifest(file with { MachineAssignmentQuality = "verified-installation" })));
    }

    [Fact]
    public async Task OversizedEncodedManifestIsRejectedBeforeWritingToTheArchive()
    {
        // The JSON encoder escapes non-ASCII characters; a character-count limit alone
        // would accept this payload even though the resulting JSON exceeds 16 MiB.
        var file = FileMetadata() with { MachineName = new string('é', (int)(OnlineLogArchiveManifestCodec.MaximumManifestBytes / 6)) };
        using var destination = new MemoryStream();
        destination.Write(new byte[] { 7, 8, 9 });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OnlineLogArchiveManifestCodec.WriteAsync(destination, Manifest(file)));
        Assert.Equal(new byte[] { 7, 8, 9 }, destination.ToArray());
    }

    private static OnlineLogArchiveManifest Manifest(OnlineLogArchiveFile file) => new() { Files = [file] };
    private static OnlineLogArchiveFile FileMetadata() => new()
    {
        ArchiveName = "22484AAA/00000072/00000001.MF4", ObjectKey = "object-key", LoggerId = "22484AAA", Session = "00000072",
        SizeBytes = 3, Sha256 = Convert.ToHexString(SHA256.HashData([1, 2, 3]))
    };

    private sealed class DownloadFixture : IDisposable
    {
        public string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "canalyser-integrity-test-" + Guid.NewGuid().ToString("N"));
        public OnlineLogService Service { get; }
        public byte[] Bytes { get; init; } = [1, 2, 3];
        public string? PlanKey { get; init; }
        public bool DuplicatePlan { get; init; }
        public bool AdvertiseExpectedLength { get; init; }
        public int Downloads { get; private set; }
        public OnlineLogSelection Selection { get; } = new("22484AAA/00000072/00000001.MF4", "00000001.MF4", "22484AAA", "00000072", 3)
        {
            RawRecordedAtUtc = DateTimeOffset.Parse("2026-10-06T10:00:00Z"),
            UploadedAtUtc = DateTimeOffset.Parse("2026-10-19T10:00:00Z"),
            MachineName = "Vlindermachine 2", ObjectVersion = "object-v1", MetadataRevision = "1"
        };

        public DownloadFixture()
        {
            Service = new OnlineLogService(new HttpClient(new Handler(request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    var entry = new { key = PlanKey, archiveName = Selection.Key, url = "https://storage.example.test/data" };
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { files = DuplicatePlan ? new[] { entry, entry } : [entry] }) };
                }
                Downloads++;
                var content = new ByteArrayContent(Bytes);
                if (AdvertiseExpectedLength) content.Headers.ContentLength = Selection.SizeBytes;
                return new(HttpStatusCode.OK) { Content = content };
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
