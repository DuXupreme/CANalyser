using System.Globalization;
using System.IO.Compression;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Parsing;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class MachineSourceTimestampTests
{
    private static readonly DateTimeOffset Handover = new(2026, 10, 15, 9, 18, 14, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReorderedConversionRetainsEachSourceMeasurementTime(bool reverse)
    {
        var root = Path.Combine(Path.GetTempPath(), "canalyser-source-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "sources.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                archive.CreateEntry("48EDFD35/00000001/00000001.MF4");
                archive.CreateEntry("48EDFD35/00000002/00000001.MF4");
            }
            var parser = new Mdf4Parser(new TimestampConverter(reverse), new PeakTrcParser());
            var result = await parser.ParseAsync(zipPath, ImportMode.Strict, null, default);
            Assert.NotNull(result);
            try
            {
                Assert.Equal(Handover.AddSeconds(-9), result.SourceFiles.Single(file => file.Session == "00000001").RecordedAt);
                // Header is before the handover, first record is after it.
                Assert.Equal(Handover.AddSeconds(1), result.SourceFiles.Single(file => file.Session == "00000002").RecordedAt);
                Assert.All(result.SourceFiles, source => Assert.Equal("00000001.MF4", source.Name));
                Assert.Equal(Handover.AddSeconds(-10), result.StartTimeUtc);
            }
            finally { (result.Frames as IDisposable)?.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnmatchedConversionDoesNotGuessSourceTimeFromOutputOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "canalyser-unknown-source-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "sources.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                archive.CreateEntry("48EDFD35/00000001/00000001.MF4");
                archive.CreateEntry("48EDFD35/00000002/00000001.MF4");
            }
            var result = await new Mdf4Parser(new TimestampConverter(true, unmatched: true), new PeakTrcParser())
                .ParseAsync(zipPath, ImportMode.Strict, null, default);
            Assert.NotNull(result);
            try { Assert.All(result.SourceFiles, source => Assert.Null(source.RecordedAt)); }
            finally { (result.Frames as IDisposable)?.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TimestampConverter(bool reverse, bool unmatched = false) : IMdf4ConversionService
    {
        public async Task<IReadOnlyList<string>> ConvertToPeakTrcAsync(IReadOnlyList<string> inputPaths,
            string outputDirectory, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
        {
            var outputs = new List<string>();
            for (var index = 0; index < inputPaths.Count; index++)
            {
                var directory = Path.Combine(outputDirectory, "batch-" + (index + 1));
                Directory.CreateDirectory(directory);
                var name = unmatched ? "unknown-" + index + ".trc" : Path.GetFileNameWithoutExtension(inputPaths[index]) + "_CAN.trc";
                var path = Path.Combine(directory, name);
                var origin = Handover.AddSeconds(index == 0 ? -10 : -1).UtcDateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture);
                var milliseconds = index == 0 ? "1000.000" : "2000.000";
                await File.WriteAllTextAsync(path,
                    $";$FILEVERSION=1.1\n;$STARTTIME={origin}\n1) {milliseconds} Rx 123 1 AA\n", cancellationToken);
                outputs.Add(path);
            }
            if (reverse) outputs.Reverse();
            return outputs;
        }
    }
}
