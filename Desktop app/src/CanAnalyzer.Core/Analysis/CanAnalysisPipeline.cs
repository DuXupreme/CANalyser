using System.Reflection;
using System.Security.Cryptography;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CanAnalyzer.Core.Analysis;

public sealed class CanAnalysisPipeline : ICanAnalysisPipeline
{
    private readonly ICanLogParsingService _parsingService;
    private readonly IDbcLoader _dbcLoader;
    private readonly ICanDecodingService _decodingService;
    private readonly IDatasetBuilder _datasetBuilder;
    private readonly ILogger<CanAnalysisPipeline> _logger;

    public CanAnalysisPipeline(ICanLogParsingService parsingService, IDbcLoader dbcLoader,
        ICanDecodingService decodingService, IDatasetBuilder datasetBuilder, ILogger<CanAnalysisPipeline> logger)
    {
        _parsingService = parsingService;
        _dbcLoader = dbcLoader;
        _decodingService = decodingService;
        _datasetBuilder = datasetBuilder;
        _logger = logger;
    }

    public async Task<CanDataset> LoadAsync(
        string logFilePath, string dbcFilePath, ImportMode importMode,
        IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting strict load/decode: log={LogFile}, dbc={DbcFile}, mode={Mode}", logFilePath, dbcFilePath, importMode);
        progress?.Report(new LoadProgress("Logbestand valideren en inlezen...", 2));
        var parseResult = await _parsingService.ParseAsync(logFilePath, importMode, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new LoadProgress("DBC valideren...", 15));
        var database = await _dbcLoader.LoadAsync(dbcFilePath, cancellationToken).ConfigureAwait(false);
        var combinedReport = database.Issues.Count == 0
            ? parseResult.Report
            : parseResult.Report with { Issues = parseResult.Report.Issues.Concat(database.Issues).ToList() };
        if (importMode == ImportMode.Strict && database.Issues.Any(static issue => issue.Severity == ImportIssueSeverity.Error))
        {
            (parseResult.Frames as IDisposable)?.Dispose();
            throw new ImportIntegrityException(
                $"DBC-validatie is mislukt met {database.Issues.Count:N0} fout(en).",
                combinedReport);
        }

        var completeness = parseResult.Completeness == DatasetCompleteness.Partial || database.Issues.Count > 0
            ? DatasetCompleteness.Partial
            : DatasetCompleteness.Complete;
        progress?.Report(new LoadProgress("Strikt decoderen...", 20));
        var decodeResult = await Task.Run(
            () => _decodingService.Decode(parseResult.Frames, database, progress, cancellationToken), cancellationToken).ConfigureAwait(false);

        var decodeIssues = BuildDecodeIssues(decodeResult.Diagnostics);
        if (decodeIssues.Count > 0)
        {
            combinedReport = combinedReport with
            {
                Issues = combinedReport.Issues.Concat(decodeIssues).ToList()
            };
            completeness = DatasetCompleteness.Partial;
            if (importMode == ImportMode.Strict)
            {
                (parseResult.Frames as IDisposable)?.Dispose();
                (decodeResult.Samples as IDisposable)?.Dispose();
                throw new ImportIntegrityException(
                    $"Decodering is mislukt voor {decodeResult.Diagnostics.DecodeErrorFrameCount:N0} frame(s); " +
                    $"{decodeResult.Diagnostics.AmbiguousFrameCount:N0} frame(s) waren ambigu.",
                    combinedReport);
            }
        }

        progress?.Report(new LoadProgress("Dataset cache opbouwen...", 92));
        var logHashTask = ComputeSha256Async(logFilePath, cancellationToken);
        var dbcHashTask = ComputeSha256Async(dbcFilePath, cancellationToken);
        await Task.WhenAll(logHashTask, dbcHashTask).ConfigureAwait(false);
        var entryAssembly = Assembly.GetEntryAssembly();
        var version = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                      ?? entryAssembly?.GetName().Version?.ToString(3)
                      ?? "2.0.1";
        var dataset = await Task.Run(() => _datasetBuilder.Build(
            parseResult.Frames, decodeResult.Samples, decodeResult.MessageSummaries, decodeResult.Diagnostics,
            combinedReport, completeness, logHashTask.Result, dbcHashTask.Result, version), cancellationToken).ConfigureAwait(false);
        progress?.Report(new LoadProgress("Klaar.", 100));
        return dataset;
    }

    private static IReadOnlyList<ImportIssue> BuildDecodeIssues(DecoderDiagnostics diagnostics)
    {
        var issues = new List<ImportIssue>(2);
        if (diagnostics.DecodeErrorFrameCount > 0)
        {
            issues.Add(new ImportIssue(
                ImportIssueSeverity.Error,
                "DECODE_ERROR",
                "DBC decoder",
                -1,
                $"{diagnostics.DecodeErrorFrameCount:N0} frame(s) konden niet lossless worden gedecodeerd; er zijn geen vervangende waarden aangemaakt.",
                string.Empty));
        }

        if (diagnostics.AmbiguousFrameCount > 0)
        {
            issues.Add(new ImportIssue(
                ImportIssueSeverity.Error,
                "AMBIGUOUS_J1939",
                "DBC decoder",
                -1,
                $"{diagnostics.AmbiguousFrameCount:N0} frame(s) hadden meerdere mogelijke J1939-PGN-matches en zijn niet gedecodeerd.",
                string.Empty));
        }

        return issues;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
