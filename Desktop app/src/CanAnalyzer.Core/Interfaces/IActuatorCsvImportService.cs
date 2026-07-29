using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>Imports one or more Actuator Testbench CSV logs as aligned signal series.</summary>
public interface IActuatorCsvImportService
{
    Task<CanDataset> ImportAsync(
        IReadOnlyList<string> filePaths,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
