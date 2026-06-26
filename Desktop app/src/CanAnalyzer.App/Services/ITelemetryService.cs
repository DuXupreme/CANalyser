using CanAnalyzer.App.State;

namespace CanAnalyzer.App.Services;

/// <summary>
/// Records privacy-minimal usage telemetry when the user has enabled it.
/// </summary>
public interface ITelemetryService
{
    string LocalLogPath { get; }

    string InstallationId { get; }

    void Configure(TelemetryOptions options);

    Task TrackEventAsync(
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null,
        CancellationToken cancellationToken = default);
}
