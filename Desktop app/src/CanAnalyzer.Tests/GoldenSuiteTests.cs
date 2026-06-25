using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class GoldenSuiteTests
{
    [Fact]
    public async Task LocalGoldenCssLogs_ParseEverySourceLineAndPreserveChecksums()
    {
        var manifest = TryLoadGoldenManifest();
        if (manifest is null) return;

        var service = CreateParsingService();
        foreach (var fixture in manifest.Logs)
        {
            var logPath = Path.Combine(manifest.Root, fixture.FileName);
            var result = await service.ParseAsync(logPath, ImportMode.Strict, null, CancellationToken.None);
            try
            {
                Assert.Equal("CSS/CL1000", result.Report.ParserName);
                Assert.Equal(fixture.TotalLines, result.Report.TotalLines);
                Assert.Equal(fixture.NonDataLines, result.Report.NonDataLines);
                Assert.Equal(fixture.AcceptedFrames, result.Report.AcceptedLines);
                Assert.Equal(0, result.Report.RejectedLines);
                Assert.True(result.Report.IsConsistent);
                Assert.Equal(DatasetCompleteness.Complete, result.Completeness);
                Assert.Equal(fixture.AcceptedFrames, result.Frames.Count);
                Assert.Equal(fixture.Sha256, await Sha256Async(logPath));
            }
            finally
            {
                (result.Frames as IDisposable)?.Dispose();
            }
        }
    }

    [Fact]
    public async Task LocalGoldenTrowelLogs_DecodeCountsMatchIndependentReference()
    {
        var manifest = TryLoadGoldenManifest();
        if (manifest is null) return;

        foreach (var fixture in manifest.Logs)
        {
            var dataset = await CreatePipeline().LoadAsync(
                Path.Combine(manifest.Root, fixture.FileName),
                Path.Combine(manifest.Root, manifest.PrimaryDbcFileName),
                ImportMode.Partial,
                null,
                CancellationToken.None);

            using (dataset)
            {
                Assert.Equal(fixture.AcceptedFrames, dataset.RawCount);
                Assert.Equal(fixture.AcceptedFrames, dataset.ImportReport?.AcceptedLines);
                Assert.Equal(DatasetCompleteness.Partial, dataset.Completeness);
                Assert.Equal(manifest.PrimaryDbcSha256, dataset.DbcSha256);
                Assert.Equal(fixture.Sha256, dataset.SourceLogSha256);
                Assert.Equal(fixture.DecodedFrames, dataset.MessageSummaries.Sum(summary => summary.Count));
                Assert.Equal(fixture.DecodedSamples, dataset.DecodedSamples.Count);
                Assert.Equal(fixture.UnmatchedFrames, dataset.Diagnostics.UnmatchedFrameCount);
                Assert.Equal(fixture.DecodeErrors, dataset.Diagnostics.DecodeErrorFrameCount);
                Assert.Equal(0, dataset.Diagnostics.AmbiguousFrameCount);
                Assert.True(dataset.SignalSeriesByIdentity.Count > 0);
                Assert.All(dataset.SignalSeriesByIdentity.Keys, identity => Assert.False(string.IsNullOrWhiteSpace(identity.SignalName)));
            }
        }
    }

    [Fact]
    public async Task LocalGoldenTrowelLog_StrictModeBlocksInvalidDbcInsteadOfSynthesizingValues()
    {
        var manifest = TryLoadGoldenManifest();
        if (manifest is null) return;

        var first = Assert.Single(manifest.Logs.Take(1));
        var exception = await Assert.ThrowsAsync<ImportIntegrityException>(() =>
            CreatePipeline().LoadAsync(
                Path.Combine(manifest.Root, first.FileName),
                Path.Combine(manifest.Root, manifest.PrimaryDbcFileName),
                ImportMode.Strict,
                null,
                CancellationToken.None));

        Assert.Contains("DBC-validatie is mislukt", exception.Message);
        Assert.Contains(exception.Report.Issues, issue => issue.Code == "DBC_SIGNAL_LAYOUT");
    }

    private static CanAnalysisPipeline CreatePipeline() => new(
        CreateParsingService(),
        new DbcLoader(),
        new CanDecodingService(),
        new DatasetBuilder(),
        NullLogger<CanAnalysisPipeline>.Instance);

    private static CanLogParsingService CreateParsingService() => new(
        new CssSemicolonParser(),
        new BusmasterParser(),
        new PeakTrcParser(),
        new CandumpParser(),
        new GenericTextCanParser(),
        NullLogger<CanLogParsingService>.Instance);

    private static GoldenManifest? TryLoadGoldenManifest()
    {
        var root = TryFindGoldenRoot();
        if (root is null) return null;

        var manifestPath = Path.Combine(root, "manifest.csv");
        if (!File.Exists(manifestPath)) return null;

        var logs = new List<GoldenLogFixture>();
        string? primaryDbc = null;
        string? primaryDbcSha256 = null;

        foreach (var rawLine in File.ReadLines(manifestPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var columns = line.Split(',', StringSplitOptions.TrimEntries);
            if (columns.Length == 0) continue;

            if (columns[0].Equals("primary_dbc", StringComparison.OrdinalIgnoreCase))
            {
                if (columns.Length != 3) throw new InvalidDataException("primary_dbc rows must contain: primary_dbc,file,sha256.");
                primaryDbc = columns[1];
                primaryDbcSha256 = columns[2];
                continue;
            }

            if (columns[0].Equals("log", StringComparison.OrdinalIgnoreCase))
            {
                if (columns.Length != 10)
                {
                    throw new InvalidDataException(
                        "log rows must contain: log,file,totalLines,nonDataLines,acceptedFrames,sha256,decodedFrames,decodedSamples,unmatchedFrames,decodeErrors.");
                }

                logs.Add(new GoldenLogFixture(
                    columns[1],
                    int.Parse(columns[2], System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(columns[3], System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(columns[4], System.Globalization.CultureInfo.InvariantCulture),
                    columns[5],
                    int.Parse(columns[6], System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(columns[7], System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(columns[8], System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(columns[9], System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        if (primaryDbc is null || primaryDbcSha256 is null || logs.Count == 0)
        {
            return null;
        }

        return new GoldenManifest(root, primaryDbc, primaryDbcSha256, logs);
    }

    private static string? TryFindGoldenRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "testdata", "golden");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        return null;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private sealed record GoldenManifest(
        string Root,
        string PrimaryDbcFileName,
        string PrimaryDbcSha256,
        IReadOnlyList<GoldenLogFixture> Logs);

    private sealed record GoldenLogFixture(
        string FileName,
        int TotalLines,
        int NonDataLines,
        int AcceptedFrames,
        string Sha256,
        int DecodedFrames,
        int DecodedSamples,
        int UnmatchedFrames,
        int DecodeErrors);
}
