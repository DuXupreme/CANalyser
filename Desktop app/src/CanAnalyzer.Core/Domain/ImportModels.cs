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
    public bool IsConsistent => TotalLines == NonDataLines + AcceptedLines + RejectedLines;
    public bool HasErrors => Issues.Any(static issue => issue.Severity == ImportIssueSeverity.Error);
}

public sealed record CanLogParseResult(
    IReadOnlyList<RawCanFrame> Frames,
    ImportReport Report,
    DatasetCompleteness Completeness);

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
