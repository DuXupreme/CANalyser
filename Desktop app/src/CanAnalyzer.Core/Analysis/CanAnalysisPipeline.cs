using System.Diagnostics;
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

    public async Task<PreparedCanAnalysis> PrepareForReviewAsync(
        string logFilePath, string dbcFilePath, IProgress<LoadProgress>? progress, CancellationToken cancellationToken) =>
        new(await LoadCoreAsync(logFilePath, dbcFilePath, ImportMode.Partial, progress, cancellationToken, forReview: true).ConfigureAwait(false));

    public Task<CanDataset> LoadAsync(
        string logFilePath, string dbcFilePath, ImportMode importMode,
        IProgress<LoadProgress>? progress, CancellationToken cancellationToken) =>
        LoadCoreAsync(logFilePath, dbcFilePath, importMode, progress, cancellationToken);

    private async Task<CanDataset> LoadCoreAsync(
        string logFilePath, string dbcFilePath, ImportMode importMode,
        IProgress<LoadProgress>? progress, CancellationToken cancellationToken, bool forReview = false)
    {
        _logger.LogInformation("Starting strict load/decode: log={LogFile}, dbc={DbcFile}, mode={Mode}", logFilePath, dbcFilePath, importMode);
        progress?.Report(new LoadProgress("Logbestand valideren en inlezen...", 2));
        var timer = Stopwatch.StartNew();
        var parseResult = await _parsingService.ParseAsync(logFilePath, importMode, progress, cancellationToken).ConfigureAwait(false);
        var parseMs = timer.ElapsedMilliseconds;
        DecodeResult? decodeResult = null;
        try
        {
            progress?.Report(new LoadProgress("DBC valideren...", 15));
            timer.Restart();
            var database = await _dbcLoader.LoadAsync(dbcFilePath, cancellationToken).ConfigureAwait(false);
            var dbcMs = timer.ElapsedMilliseconds;
            var combinedReport = database.Issues.Count == 0
                ? parseResult.Report
                : parseResult.Report with { Issues = parseResult.Report.Issues.Concat(database.Issues).ToList() };
            if (importMode == ImportMode.Strict && database.Issues.Any(static issue => issue.Severity == ImportIssueSeverity.Error))
            {
                throw new ImportIntegrityException(
                    $"DBC-validatie is mislukt met {database.Issues.Count:N0} fout(en).",
                    combinedReport);
            }

            var completeness = parseResult.Completeness == DatasetCompleteness.Partial || database.Issues.Count > 0
                ? DatasetCompleteness.Partial
                : DatasetCompleteness.Complete;
            progress?.Report(new LoadProgress("Strikt decoderen...", 20));
            timer.Restart();
            decodeResult = await Task.Run(
                () => _decodingService.Decode(parseResult.Frames, database, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            var decodeMs = timer.ElapsedMilliseconds;

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
                    throw new ImportIntegrityException(
                        $"Decodering is mislukt voor {decodeResult.Diagnostics.DecodeErrorFrameCount:N0} frame(s); " +
                        $"{decodeResult.Diagnostics.AmbiguousFrameCount:N0} frame(s) waren ambigu.",
                        combinedReport);
                }
            }

            progress?.Report(new LoadProgress("Dataset cache opbouwen...", 92));
            timer.Restart();
            var logHashTask = ComputeSha256Async(logFilePath, cancellationToken);
            var dbcHashTask = ComputeSha256Async(dbcFilePath, cancellationToken);
            await Task.WhenAll(logHashTask, dbcHashTask).ConfigureAwait(false);
            var hashMs = timer.ElapsedMilliseconds;
            var entryAssembly = Assembly.GetEntryAssembly();
            var version = entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                          ?? entryAssembly?.GetName().Version?.ToString(3)
                          ?? "2.1.0";
            if (forReview && !combinedReport.HasErrors)
                combinedReport = combinedReport with { Mode = ImportMode.Strict };
            timer.Restart();
            var dataset = await Task.Run(() => _datasetBuilder.Build(
                parseResult.Frames, decodeResult.Samples, decodeResult.MessageSummaries, decodeResult.Diagnostics,
                combinedReport, completeness, logHashTask.Result, dbcHashTask.Result, version, parseResult.StartTimeUtc), cancellationToken).ConfigureAwait(false);
            dataset.LoadTimings = new LoadTimings(parseMs, dbcMs, decodeMs, hashMs, timer.ElapsedMilliseconds);
            dataset.SourceLogPath = Path.GetFullPath(logFilePath);
            dataset.SourceDbcPath = Path.GetFullPath(dbcFilePath);
            dataset.SourceFiles = parseResult.SourceFiles.Count > 0
                ? parseResult.SourceFiles : [SourceLogFile.FromLocalFile(dataset.SourceLogPath)];
            _logger.LogInformation("Load phases (ms): parse={Parse}, dbc={Dbc}, decode={Decode}, hash={Hash}, dataset={Dataset}",
                parseMs, dbcMs, decodeMs, hashMs, dataset.LoadTimings.DatasetMilliseconds);
            progress?.Report(new LoadProgress("Klaar.", 100));
            return dataset;
        }
        catch
        {
            (decodeResult?.Samples as IDisposable)?.Dispose();
            (parseResult.Frames as IDisposable)?.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<ImportIssue> BuildDecodeIssues(DecoderDiagnostics diagnostics)
    {
        var issues = new List<ImportIssue>();
        if (diagnostics.DecodeErrorFrameCount > 0)
        {
            var failures = diagnostics.DecodeFailures ?? [];
            if (failures.Count == 0)
            {
                issues.Add(new ImportIssue(
                    ImportIssueSeverity.Error,
                    "DECODE_ERROR",
                    "DBC decoder",
                    -1,
                    $"{diagnostics.DecodeErrorFrameCount:N0} frame(s) konden niet lossless worden gedecodeerd; er zijn geen vervangende waarden aangemaakt.",
                    string.Empty));
            }
            else
            {
                issues.AddRange(failures.Select(BuildDecodeFailureIssue));
            }
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

    private static ImportIssue BuildDecodeFailureIssue(DecodeFailureSummary failure)
    {
        var messageName = failure.MessageNames.FirstOrDefault() ?? "Onbekend DBC-frame";
        var dbcFrameId = failure.DbcFrameId ?? failure.ObservedFrameId;
        var prefix = $"{messageName} (0x{dbcFrameId:X}): ";
        var message = failure.Kind switch
        {
            DecodeFailureKind.DlcMismatch =>
                $"{prefix}{failure.Count:N0} frame(s) hebben payloadlengte {failure.ActualPayloadLength} byte(s); " +
                $"de DBC verwacht {string.Join(", ", failure.ExpectedPayloadLengths)} byte(s). " +
                $"De waargenomen log-ID is 0x{failure.ObservedFrameId:X}.",
            DecodeFailureKind.SignalExtraction =>
                $"{prefix}{failure.Count:N0} frame(s) hebben de verwachte payloadlengte, maar " +
                (string.IsNullOrWhiteSpace(failure.FailingSignalName)
                    ? "geen actief signaal kon betrouwbaar uit de payload worden gelezen."
                    : $"signaal {failure.FailingSignalName} kon niet uit de payload worden gelezen."),
            DecodeFailureKind.SuppressedDefinition =>
                $"{prefix}{failure.Count:N0} frame(s) zijn niet gedecodeerd omdat de bijbehorende DBC-definitie door de semantische validator is geblokkeerd.",
            DecodeFailureKind.FrameFormatMismatch =>
                $"{prefix}{failure.Count:N0} frame(s) hebben een ander standaard/extended-formaat dan de bijbehorende DBC-definitie.",
            _ => $"{prefix}{failure.Count:N0} frame(s) konden niet lossless worden gedecodeerd."
        };

        return new ImportIssue(
            ImportIssueSeverity.Error,
            "DECODE_ERROR",
            "DBC decoder",
            -1,
            message,
            string.Empty);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
