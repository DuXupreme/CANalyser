using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>
/// Exports decoded signal table to CSV.
/// </summary>
public interface ICsvExportService
{
    Task ExportDecodedSignalsAsync(
        string filePath,
        IEnumerable<DecodedSignalSample> samples,
        CancellationToken cancellationToken);
}
