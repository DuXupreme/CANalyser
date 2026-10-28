namespace CanAnalyzer.Core.Domain;

public enum DatasetCompleteness
{
    Complete,
    Partial
}

public enum ImportMode
{
    Strict,
    Partial
}

public enum ImportIssueSeverity
{
    Warning,
    Error
}

public sealed record ImportIssue(
    ImportIssueSeverity Severity,
    string Code,
    string Parser,
    long SourceLineNumber,
    string Message,
    string SourceLine);

public sealed record ImportReport(
    string ParserName,
    long TotalLines,
    long NonDataLines,
    long AcceptedLines,
    long RejectedLines,
    IReadOnlyList<ImportIssue> Issues,
    ImportMode Mode = ImportMode.Strict)
{
    public IReadOnlyList<MeasurementGap> Gaps { get; init; } = [];

    /// <summary>Deliberate, version-bound exclusions. No duration is inferred from their metadata.</summary>
    public IReadOnlyList<OnlineLogArchiveOmission> Exclusions { get; init; } = [];

    /// <summary>Verified elapsed boundaries between included segments separated by exclusions.</summary>
    public IReadOnlyList<MeasurementContinuityBreak> ContinuityBreaks { get; init; } = [];

    /// <summary>Observed frame-free intervals per bus; an explicit empty list overrides global gaps.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MeasurementGap>> GapsByChannel { get; init; } =
        new Dictionary<string, IReadOnlyList<MeasurementGap>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Logger-internal buses, identified by the source format, retained raw but excluded from machine decoding.</summary>
    public IReadOnlyList<string> InternalDiagnosticChannels { get; init; } = [];

    public IReadOnlyList<MeasurementGap> GetGapsForChannel(string? channel)
    {
        if (channel is not null)
        {
            if (GapsByChannel.TryGetValue(channel, out var gaps)) return gaps;
            // Deserializers may restore a dictionary without its original comparer.
            foreach (var entry in GapsByChannel)
                if (string.Equals(entry.Key, channel, StringComparison.OrdinalIgnoreCase)) return entry.Value;
        }
        return Gaps;
    }

    public bool IsConsistent => TotalLines == NonDataLines + AcceptedLines + RejectedLines;
    public bool HasErrors => Issues.Any(static issue => issue.Severity == ImportIssueSeverity.Error);
}

public sealed record CanLogParseResult(
    IReadOnlyList<RawCanFrame> Frames,
    ImportReport Report,
    DatasetCompleteness Completeness,
    DateTimeOffset? StartTimeUtc = null)
{
    public IReadOnlyList<SourceLogFile> SourceFiles { get; init; } = [];
}

public sealed class ImportIntegrityException : Exception
{
    public ImportIntegrityException(string message, ImportReport report)
        : base(message)
    {
        Report = report;
    }

    public ImportReport Report { get; }
}

public enum AnalysisStatus
{
    Valid,
    PartialDataset,
    InsufficientOverlap,
    GapExceeded,
    ResourceLimitExceeded
}

public sealed record AnalysisResult<T>(
    T Value,
    AnalysisStatus Status,
    int SourceSampleCount,
    int AlignedSampleCount,
    long OverlapNanoseconds,
    double CoveragePercent,
    string Note);

public sealed record MeasurementGap(double StartSeconds, double EndSeconds);

public sealed record MeasurementContinuityBreak(int BeforeSourceFileIndex, int AfterSourceFileIndex,
    long BeforeTimestampNanoseconds, long AfterTimestampNanoseconds, IReadOnlyList<string> OmittedObjectKeys);
