using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Export;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Exports decoded signal table to CSV.
/// </summary>
public interface ICsvExportService
{
    Task ExportDecodedSignalsAsync(
        string filePath,
        CanDataset dataset,
        CancellationToken cancellationToken);

    Task ExportDecodedSignalsAsync(string filePath, CanDataset dataset, CsvExportOptions options,
        CancellationToken cancellationToken);
}
