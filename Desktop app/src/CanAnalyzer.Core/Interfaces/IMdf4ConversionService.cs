using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.Core.Interfaces;

/// <summary>Converts one or more CANedge MDF4 files to PEAK TRC without interpreting CAN payloads.</summary>
public interface IMdf4ConversionService
{
    Task<IReadOnlyList<string>> ConvertToPeakTrcAsync(
        IReadOnlyList<string> inputPaths,
        string outputDirectory,
        IProgress<LoadProgress>? progress,
        CancellationToken cancellationToken);
}
