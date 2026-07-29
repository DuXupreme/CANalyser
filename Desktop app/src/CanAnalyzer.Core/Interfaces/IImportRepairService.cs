using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Creates traceable repaired copies for import errors that can be resolved without inventing data.
/// </summary>
public interface IImportRepairService
{
    bool IsRemovableLogIssue(ImportIssue issue);

    IReadOnlyList<ImportIssue> GetRemovableLogIssues(ImportReport report);

    string GetDefaultRepairedPath(string sourcePath);

    Task<int> CreateRepairedLogCopyAsync(
        string sourcePath,
        string destinationPath,
        ImportReport report,
        CancellationToken cancellationToken);
}
