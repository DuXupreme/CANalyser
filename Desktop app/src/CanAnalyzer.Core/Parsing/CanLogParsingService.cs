using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CanAnalyzer.Core.Parsing;

/// <inheritdoc />
public sealed class CanLogParsingService : ICanLogParsingService
{
    private readonly IReadOnlyDictionary<Type, ICanLogParser> _parserByType;
    private readonly ILogger<CanLogParsingService> _logger;

    public CanLogParsingService(
        CssSemicolonParser cssParser,
        BusmasterParser busmasterParser,
        PeakTrcParser peakParser,
        CandumpParser candumpParser,
        GenericTextCanParser genericParser,
        ILogger<CanLogParsingService> logger)
    {
        _parserByType = new Dictionary<Type, ICanLogParser>
        {
            [typeof(CssSemicolonParser)] = cssParser,
            [typeof(BusmasterParser)] = busmasterParser,
            [typeof(PeakTrcParser)] = peakParser,
            [typeof(CandumpParser)] = candumpParser,
            [typeof(GenericTextCanParser)] = genericParser
        };
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawCanFrame>> ParseAsync(
        string filePath,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        var parserOrder = BuildParserOrder(fileName);

        foreach (var parser in parserOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Trying parser {ParserName} for {FilePath}", parser.Name, filePath);
            progress?.Report(new LoadProgress($"Parser starten: {parser.Name}", 5));
            var rows = await parser.ParseAsync(filePath, progress, cancellationToken);
            if (rows is { Count: > 0 })
            {
                _logger.LogInformation(
                    "Parser {ParserName} recognized {FrameCount} frames in {FilePath}",
                    parser.Name,
                    rows.Count,
                    filePath);
                return rows;
            }
        }

        throw new InvalidDataException(
            "Kon geen CAN frames herkennen. Ondersteunde formaten: PEAK .trc, BUSMASTER .log/.txt, CSS/CL1000 .txt (Timestamp;Type;ID;Data), en candump.");
    }

    private IReadOnlyList<ICanLogParser> BuildParserOrder(string fileName)
    {
        if (fileName.EndsWith(".trc", StringComparison.Ordinal))
        {
            return
            [
                _parserByType[typeof(PeakTrcParser)],
                _parserByType[typeof(BusmasterParser)],
                _parserByType[typeof(CssSemicolonParser)],
                _parserByType[typeof(CandumpParser)],
                _parserByType[typeof(GenericTextCanParser)]
            ];
        }

        if (fileName.EndsWith(".log", StringComparison.Ordinal) || fileName.EndsWith(".log.txt", StringComparison.Ordinal))
        {
            return
            [
                _parserByType[typeof(BusmasterParser)],
                _parserByType[typeof(CssSemicolonParser)],
                _parserByType[typeof(PeakTrcParser)],
                _parserByType[typeof(CandumpParser)],
                _parserByType[typeof(GenericTextCanParser)]
            ];
        }

        if (fileName.EndsWith(".txt", StringComparison.Ordinal))
        {
            return
            [
                _parserByType[typeof(CssSemicolonParser)],
                _parserByType[typeof(BusmasterParser)],
                _parserByType[typeof(PeakTrcParser)],
                _parserByType[typeof(CandumpParser)],
                _parserByType[typeof(GenericTextCanParser)]
            ];
        }

        return
        [
            _parserByType[typeof(CssSemicolonParser)],
            _parserByType[typeof(BusmasterParser)],
            _parserByType[typeof(PeakTrcParser)],
            _parserByType[typeof(CandumpParser)],
            _parserByType[typeof(GenericTextCanParser)]
        ];
    }
}
