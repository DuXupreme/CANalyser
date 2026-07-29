using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Services;

public enum ImportRepairDecision
{
    Cancel,
    ContinuePartial,
    RetryStrict
}

public sealed record ImportRepairWizardResult(
    ImportRepairDecision Decision,
    bool RemoveRejectedLogLines,
    string LogPath,
    string DbcPath);

public interface IImportRepairWizardService
{
    Task<ImportRepairWizardResult> ShowAsync(
        ImportReport report,
        string logPath,
        string dbcPath,
        CancellationToken cancellationToken);
}
