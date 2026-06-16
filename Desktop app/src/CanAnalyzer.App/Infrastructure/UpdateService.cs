using System;
using System.Reflection;
using CanAnalyzer.App.Services;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace CanAnalyzer.App.Infrastructure;

/// <inheritdoc />
public sealed class UpdateService : IUpdateService
{
    // Update-feed: GitHub Releases van de repo. Releases moeten publiek zijn,
    // anders is runtime een access-token nodig.
    private const string RepoUrl = "https://github.com/DuXupreme/CANalyser";

    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager _manager;
    private UpdateInfo? _pendingUpdate;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "onbekend";

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!_manager.IsInstalled)
        {
            return new UpdateCheckResult(false, null, "Niet via de installer geïnstalleerd; updates zijn uitgeschakeld.");
        }

        try
        {
            _pendingUpdate = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pendingUpdate is null)
            {
                return new UpdateCheckResult(false, null, null);
            }

            return new UpdateCheckResult(true, _pendingUpdate.TargetFullRelease.Version.ToString(), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Controle op updates is mislukt.");
            return new UpdateCheckResult(false, null, ex.Message);
        }
    }

    public async Task DownloadAndApplyAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        await _manager.DownloadUpdatesAsync(_pendingUpdate, cancelToken: cancellationToken).ConfigureAwait(false);

        // Herstart de app op de nieuwe versie. Keert niet terug bij succes.
        _manager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
