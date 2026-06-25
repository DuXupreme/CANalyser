using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Parsing;

internal sealed class ParseReportBuilder(string parserName)
{
    private readonly List<ImportIssue> _issues = [];

    public long TotalLines { get; private set; }
    public long NonDataLines { get; private set; }
    public long AcceptedLines { get; private set; }
    public long RejectedLines { get; private set; }

    public long NextLine() => ++TotalLines;
    public void NonData() => NonDataLines++;
    public void Accepted() => AcceptedLines++;

    public void Reject(long lineNumber, string code, string message, string sourceLine)
    {
        RejectedLines++;
        _issues.Add(new ImportIssue(ImportIssueSeverity.Error, code, parserName, lineNumber, message, sourceLine));
    }

    public void Warn(long lineNumber, string code, string message, string sourceLine) =>
        _issues.Add(new ImportIssue(ImportIssueSeverity.Warning, code, parserName, lineNumber, message, sourceLine));

    public ImportReport Build(ImportMode mode) => new(
        parserName,
        TotalLines,
        NonDataLines,
        AcceptedLines,
        RejectedLines,
        _issues,
        mode);
}
