using CanAnalyzer.App.State;

namespace CanAnalyzer.App.Services;

/// <summary>
/// Persists app settings as JSON.
/// </summary>
public interface IAppSettingsStore
{
    string SettingsPath { get; }

    AppSettings Load();

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}
