using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CanAnalyzer.Core.Parsing;

/// <summary>Selects exactly one parser using format probes and enforces the import integrity policy.</summary>
public sealed class CanLogParsingService : ICanLogParsingService
{
    private readonly IReadOnlyList<ICanLogParser> _automaticParsers;
    private readonly ILogger<CanLogParsingService> _logger;

    public CanLogParsingService(
        CssSemicolonParser cssParser,
        BusmasterParser busmasterParser,
        PeakTrcParser peakParser,
        CandumpParser candumpParser,
        GenericTextCanParser genericParser,
        ILogger<CanLogParsingService> logger)
    {
        _automaticParsers = [cssParser, busmasterParser, peakParser, candumpParser];
        _logger = logger;
        GenericParser = genericParser;
    }

    /// <summary>The generic parser is intentionally not part of automatic detection.</summary>
    public GenericTextCanParser GenericParser { get; }

    public async Task<CanLogParseResult> ParseAsync(
        string filePath,
        ImportMode mode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sample = new List<string>(200);
        await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            while (sample.Count < 200)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                sample.Add(line);
            }
        }

        var probes = _automaticParsers
            .Select(parser => (Parser: parser, Score: parser.Probe(filePath, sample)))
            .OrderByDescending(static result => result.Score)
            .ToList();
        var best = probes.FirstOrDefault();
        if (best.Parser is null || best.Score <= 0)
        {
            throw new InvalidDataException(
                "Het logformaat kon niet betrouwbaar worden vastgesteld. Kies een ondersteund PEAK, BUSMASTER, CSS/CL1000- of candump-bestand; generieke import is niet automatisch toegestaan.");
        }

        if (probes.Count > 1 && probes[1].Score == best.Score)
        {
            throw new InvalidDataException(
                $"Het logformaat is ambigu tussen {best.Parser.Name} en {probes[1].Parser.Name}. Import is geblokkeerd om gedeeltelijke parsing te voorkomen.");
        }

        _logger.LogInformation("Selected parser {ParserName} with confidence {Score}", best.Parser.Name, best.Score);
        progress?.Report(new LoadProgress($"Parser geselecteerd: {best.Parser.Name}", 5));
        var result = await best.Parser.ParseAsync(filePath, mode, progress, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Frames.Count == 0)
        {
            throw new InvalidDataException($"Parser {best.Parser.Name} herkende geen geldige CAN-frames.");
        }

        if (!result.Report.IsConsistent)
        {
            throw new InvalidDataException("Interne importfout: niet iedere bronregel is geclassificeerd.");
        }

        if (mode == ImportMode.Strict && result.Report.HasErrors)
        {
            (result.Frames as IDisposable)?.Dispose();
            throw new ImportIntegrityException(
                $"Import geblokkeerd: {result.Report.RejectedLines:N0} gegevensregels zijn afgewezen door {result.Report.ParserName}.",
                result.Report);
        }

        return result;
    }
}
