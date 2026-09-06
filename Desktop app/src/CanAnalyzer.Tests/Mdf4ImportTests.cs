using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class Mdf4ImportTests
{
    [Fact]
    public async Task EmbeddedConverterHasPinnedSecurityHash()
    {
        await using var stream = typeof(Mdf4ConversionService).Assembly
            .GetManifestResourceStream(Mdf4ConversionService.EmbeddedResourceName);

        Assert.NotNull(stream);
        Assert.Equal(
            Mdf4ConversionService.ExpectedSha256,
            Convert.ToHexString(await SHA256.HashDataAsync(stream!)));
    }

    [Fact]
    public async Task PeakParser_DoesNotInterpretReceiveDirectionAsExtendedFlag()
    {
        var path = Path.Combine(Path.GetTempPath(), $"peak-standard-rx-{Guid.NewGuid():N}.trc");
        await File.WriteAllTextAsync(path, ";$FILEVERSION=1.1\n1) 0.000 Rx 123 1 AA\n");
        try
        {
            var result = await new PeakTrcParser().ParseAsync(path, ImportMode.Strict, null, CancellationToken.None);
            Assert.NotNull(result);
            var frame = Assert.Single(result!.Frames);
            Assert.Equal(0x123u, frame.Id);
            Assert.False(frame.IsExtended);
            (result.Frames as IDisposable)?.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PeakParser_ReadsCssAbsoluteStartTimeAsUtc()
    {
        var path = Path.Combine(Path.GetTempPath(), $"peak-start-{Guid.NewGuid():N}.trc");
        var expected = new DateTimeOffset(2026, 9, 2, 18, 50, 0, TimeSpan.Zero);
        await File.WriteAllTextAsync(path,
            $";$FILEVERSION=1.1\n;$STARTTIME={expected.UtcDateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture)}\n1) 0.000 Rx 123 1 AA\n");
        try
        {
            var result = await new PeakTrcParser().ParseAsync(path, ImportMode.Strict, null, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(expected, result!.StartTimeUtc);
            Assert.True(result.Report.IsConsistent);
            (result.Frames as IDisposable)?.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ZipImport_MergesConsecutivePartsFromOneSessionChronologically()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"mf4-archive-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "48EDFD35/session-a/00000001-start.MF4");
            await WriteEntryAsync(archive, "48EDFD35/session-a/00000002-next.MF4");
        }

        try
        {
            var parser = new Mdf4Parser(new FakeConverter(), new PeakTrcParser());
            var result = await parser.ParseAsync(zipPath, ImportMode.Strict, null, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(3, result!.Frames.Count);
            Assert.Equal(100_000_000L, result.Frames[0].TimestampNanoseconds);
            Assert.Equal(10_200_000_000L, result.Frames[1].TimestampNanoseconds);
            Assert.Equal(20_000_000_000L, result.Frames[2].TimestampNanoseconds);
            Assert.Equal([0L, 1L, 2L], result.Frames.Select(static frame => frame.FrameIndex));
            Assert.Equal(new DateTimeOffset(2026, 9, 2, 18, 50, 0, TimeSpan.Zero), result.StartTimeUtc);
            Assert.True(result.Report.IsConsistent);
            (result.Frames as IDisposable)?.Dispose();
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ZipImport_RejectsFilesFromDifferentSessionsBeforeConversion()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"mf4-mixed-sessions-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "48EDFD35/session-a/00000001-start.MF4");
            await WriteEntryAsync(archive, "48EDFD35/session-b/00000001-start.MF4");
        }

        try
        {
            var parser = new Mdf4Parser(new FakeConverter(), new PeakTrcParser());
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                parser.ParseAsync(zipPath, ImportMode.Strict, null, CancellationToken.None));
            Assert.Contains("verschillende logger-sessies", exception.Message);
            Assert.Contains("afzonderlijk", exception.Message);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public void OnlineLogSequencePolicy_RejectsMissingPart()
    {
        var validation = OnlineLogSequencePolicy.Validate([
            new("48EDFD35", "00000008", "00000001-start.MF4"),
            new("48EDFD35", "00000008", "00000003-end.MF4")
        ]);

        Assert.False(validation.IsValid);
        Assert.Contains("niet opeenvolgend", validation.Message);
        Assert.Contains("tussenliggende delen", validation.Message);
    }

    [Fact]
    public void OnlineLogSequencePolicy_HandlesEmptyAndSingleSelections()
    {
        Assert.False(OnlineLogSequencePolicy.Validate([]).IsValid);
        Assert.True(OnlineLogSequencePolicy.Validate([
            new("logger", "session", "any-name.MF4")
        ]).IsValid);
    }

    [Theory]
    [InlineData("", "session")]
    [InlineData("logger", " ")]
    public void OnlineLogSequencePolicy_RejectsMissingSessionIdentity(string logger, string session)
    {
        var validation = OnlineLogSequencePolicy.Validate([
            new(logger, session, "00000001.MF4"),
            new(logger, session, "00000002.MF4")
        ]);

        Assert.False(validation.IsValid);
        Assert.Contains("logger-sessies", validation.Message);
    }

    [Fact]
    public void OnlineLogSequencePolicy_RejectsDuplicateAndUnrecognizableParts()
    {
        var duplicate = OnlineLogSequencePolicy.Validate([
            new("logger", "session", "00000001-first.MF4"),
            new("logger", "session", "00000001-copy.MF4")
        ]);
        var unrecognizable = OnlineLogSequencePolicy.Validate([
            new("logger", "session", "part-one.MF4"),
            new("logger", "session", "00000002.MF4")
        ]);

        Assert.False(duplicate.IsValid);
        Assert.Contains("meer dan één keer", duplicate.Message);
        Assert.False(unrecognizable.IsValid);
        Assert.Contains("bestandsnamen", unrecognizable.Message);
    }

    [Theory]
    [InlineData("", false, 0)]
    [InlineData("part.MF4", false, 0)]
    [InlineData("12part.MF4", false, 0)]
    [InlineData("12.MF4", true, 12)]
    [InlineData("12_data.MF4", true, 12)]
    [InlineData("999999999999999999999999.MF4", false, 0)]
    public void OnlineLogSequencePolicy_ParsesOnlyRecognizablePartNumbers(
        string fileName,
        bool expectedResult,
        int expectedPart)
    {
        var result = OnlineLogSequencePolicy.TryParsePartNumber(fileName, out var partNumber);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedPart, partNumber);
    }

    [Fact]
    public async Task BundledConverter_ImportsRealSample_WhenTestFileIsProvided()
    {
        var samplePath = Environment.GetEnvironmentVariable("CANALYSER_MF4_TEST_FILE");
        if (string.IsNullOrWhiteSpace(samplePath) || !File.Exists(samplePath)) return;

        var converterPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "tools", "mdf4", "mdf2peak.exe"));
        var converter = new Mdf4ConversionService(converterPath, NullLogger<Mdf4ConversionService>.Instance);
        var parser = new Mdf4Parser(converter, new PeakTrcParser());
        var result = await parser.ParseAsync(samplePath, ImportMode.Strict, null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotEmpty(result!.Frames);
        Assert.NotNull(result.StartTimeUtc);
        Assert.True(result.Report.IsConsistent);
        (result.Frames as IDisposable)?.Dispose();
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string path)
    {
        var entry = archive.CreateEntry(path);
        await using var stream = entry.Open();
        await stream.WriteAsync("not parsed by the fake converter"u8.ToArray());
    }

    private sealed class FakeConverter : IMdf4ConversionService
    {
        public async Task<IReadOnlyList<string>> ConvertToPeakTrcAsync(
            IReadOnlyList<string> inputPaths,
            string outputDirectory,
            IProgress<LoadProgress>? progress,
            CancellationToken cancellationToken)
        {
            Assert.Equal(2, inputPaths.Count);
            Assert.Equal(2, inputPaths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Directory.CreateDirectory(outputDirectory);
            var baseTime = new DateTimeOffset(2026, 9, 2, 18, 50, 0, TimeSpan.Zero);
            var outputs = new List<string>();
            for (var index = 0; index < inputPaths.Count; index++)
            {
                var output = Path.Combine(outputDirectory, $"part-{index + 1}.trc");
                var start = baseTime.AddSeconds(index * 10).UtcDateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture);
                var relativeMilliseconds = index == 0 ? "100.000" : "200.000";
                var overlappingTail = index == 0 ? "2) 20000.000 Rx 123 1 0A\n" : string.Empty;
                await File.WriteAllTextAsync(output,
                    $";$FILEVERSION=1.1\n;$STARTTIME={start}\n1) {relativeMilliseconds} Rx 123 1 0{index + 1}\n{overlappingTail}", cancellationToken);
                outputs.Add(output);
            }

            return outputs;
        }
    }
}
