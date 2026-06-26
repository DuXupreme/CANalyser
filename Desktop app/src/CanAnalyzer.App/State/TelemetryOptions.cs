namespace CanAnalyzer.App.State;

/// <summary>
/// Privacy-minimal usage telemetry settings. Telemetry is enabled by default.
/// </summary>
public sealed class TelemetryOptions
{
    public const string DefaultEndpointUrl = "https://canalyser-telemetry.42069.workers.dev/events";

    public const string DefaultEndpointKey = "";

    public bool Enabled { get; set; } = true;

    public string EndpointUrl { get; set; } = DefaultEndpointUrl;

    public string EndpointKey { get; set; } = DefaultEndpointKey;

    public string InstallationId { get; set; } = Guid.NewGuid().ToString("N");

    public int RetentionDays { get; set; } = 180;
}
