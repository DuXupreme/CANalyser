using System.Text.Json;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.State;
using System.IO;

namespace CanAnalyzer.App.Infrastructure;

/// <inheritdoc />
public sealed class AppSettingsStore : IAppSettingsStore
{
    private const int CurrentSettingsVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string SettingsPath { get; }

    public AppSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CanAnalyzer", "settings.json"))
    {
    }

    public AppSettingsStore(string settingsPath) => SettingsPath = Path.GetFullPath(settingsPath);

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return CreateCurrentSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            settings.RecentDbcFiles ??= [];
            settings.CsvExportPresets ??= [];
            settings.RecentLogFiles ??= [];
            settings.LastPlotViewOptions ??= new CanAnalyzer.Core.Domain.PlotViewOptions();
            settings.LastRawFrameFilter ??= new CanAnalyzer.Core.Domain.RawFrameFilterOptions();
            settings.Telemetry ??= new TelemetryOptions();
            if (settings.SettingsVersion < CurrentSettingsVersion)
            {
                // Version 2 changes the safe defaults to full-resolution plotting and 5,000 points.
                settings.LastPlotViewOptions.UseDownsampling = false;
                settings.LastPlotViewOptions.MaxPointsPerTrace = 5000;
                settings.SettingsVersion = CurrentSettingsVersion;
            }
            if (string.IsNullOrWhiteSpace(settings.Telemetry.InstallationId))
            {
                settings.Telemetry.InstallationId = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(settings.Telemetry.EndpointUrl))
            {
                settings.Telemetry.EndpointUrl = TelemetryOptions.DefaultEndpointUrl;
            }

            if (string.IsNullOrWhiteSpace(settings.Telemetry.EndpointKey))
            {
                settings.Telemetry.EndpointKey = TelemetryOptions.DefaultEndpointKey;
            }

            settings.Telemetry.RetentionDays = Math.Clamp(settings.Telemetry.RetentionDays, 30, 730);
            return settings;
        }
        catch
        {
            return CreateCurrentSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        settings.SettingsVersion = CurrentSettingsVersion;
        var directory = Path.GetDirectoryName(SettingsPath)
                        ?? throw new InvalidOperationException("Settings path has no directory.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        var temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Complete the new file beside the current one before replacing it.
            // A failed write or cancellation must preserve saved presets and settings.
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(SettingsPath))
                File.Replace(temporaryPath, SettingsPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, SettingsPath);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AppSettings CreateCurrentSettings() => new()
    {
        SettingsVersion = CurrentSettingsVersion
    };
}
