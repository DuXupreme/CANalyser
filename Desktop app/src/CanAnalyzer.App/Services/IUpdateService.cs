namespace CanAnalyzer.App.Services;

/// <summary>Resultaat van een update-controle.</summary>
public sealed record UpdateCheckResult(bool UpdateAvailable, string? NewVersion, string? Error);

/// <summary>
/// Controleert op nieuwe versies en past ze toe via Velopack.
/// </summary>
public interface IUpdateService
{
    /// <summary>Versie van de momenteel draaiende app.</summary>
    string CurrentVersion { get; }

    /// <summary>
    /// True wanneer de app via een Velopack-installer is geïnstalleerd.
    /// Bij een dev-build of losse exe zijn updates niet mogelijk.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>Checkt bij de feed of er een nieuwere versie beschikbaar is.</summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloadt de laatst gevonden update en herstart de app om hem toe te passen.
    /// Doet niets als er geen update klaarstaat.
    /// </summary>
    Task DownloadAndApplyAsync(CancellationToken cancellationToken = default);
}
