using System.Globalization;
using System.IO.Compression;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class Mdf4ImportTests
{
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
    public async Task ZipImport_PreservesDuplicateSessionFilesAndBuildsChronologicalTimeline()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"mf4-archive-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "48EDFD35/session-a/00000001.MF4");
            await WriteEntryAsync(archive, "48EDFD35/session-b/00000001.MF4");
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
