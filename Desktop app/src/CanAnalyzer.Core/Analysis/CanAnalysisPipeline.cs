using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CanAnalyzer.Core.Analysis;

/// <inheritdoc />
public sealed class CanAnalysisPipeline : ICanAnalysisPipeline
{
    private readonly ICanLogParsingService _parsingService;
    private readonly IDbcLoader _dbcLoader;
    private readonly ICanDecodingService _decodingService;
    private readonly IDatasetBuilder _datasetBuilder;
    private readonly ILogger<CanAnalysisPipeline> _logger;

    public CanAnalysisPipeline(
        ICanLogParsingService parsingService,
        IDbcLoader dbcLoader,
        ICanDecodingService decodingService,
        IDatasetBuilder datasetBuilder,
        ILogger<CanAnalysisPipeline> logger)
    {
        _parsingService = parsingService;
        _dbcLoader = dbcLoader;
        _decodingService = decodingService;
        _datasetBuilder = datasetBuilder;
        _logger = logger;
    }

    public async Task<CanDataset> LoadAsync(
        string logFilePath,
        string dbcFilePath,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting load/decode: log={LogFile}, dbc={DbcFile}", logFilePath, dbcFilePath);

        progress?.Report(new LoadProgress("Logbestand inlezen...", 2));
        var rawFrames = await _parsingService.ParseAsync(logFilePath, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new LoadProgress("DBC inlezen...", 15));
        var database = await _dbcLoader.LoadAsync(dbcFilePath, cancellationToken).ConfigureAwait(false);

        progress?.Report(new LoadProgress("Decoderen...", 20));
        var decodeResult = await Task.Run(
                () => _decodingService.Decode(rawFrames, database, progress, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new LoadProgress("Dataset cache opbouwen...", 92));
        var dataset = await Task.Run(
                () => _datasetBuilder.Build(rawFrames, decodeResult.Samples, decodeResult.MessageSummaries, decodeResult.Diagnostics),
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new LoadProgress("Klaar.", 100));
        _logger.LogInformation(
            "Load/decode complete: raw={RawCount}, decoded={DecodedCount}, signals={SignalCount}",
            dataset.RawCount,
            dataset.DecodedSamples.Count,
            dataset.SignalCount);

        return dataset;
    }
}
