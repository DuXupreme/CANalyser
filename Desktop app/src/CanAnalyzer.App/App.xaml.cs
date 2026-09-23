using System.Windows;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Decoding;
using CanAnalyzer.Core.Export;
using CanAnalyzer.Core.Interfaces;
using CanAnalyzer.Core.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows.Threading;

namespace CanAnalyzer.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private TelemetryService? _crashTelemetry;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        _serviceProvider = BuildServiceProvider();
        _crashTelemetry = (TelemetryService)_serviceProvider.GetRequiredService<ITelemetryService>();
        DispatcherUnhandledException += (_, args) =>
            _crashTelemetry.RecordCrash(args.Exception, "dispatcher");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.IsTerminating)
                _crashTelemetry.RecordCrash(args.ExceptionObject as Exception, "app_domain");
        };
        var window = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = window;

        void CloseSplash(object? sender, EventArgs args)
        {
            window.ContentRendered -= CloseSplash;
            splash.Close();
        }

        window.ContentRendered += CloseSplash;
        window.Show();

        RegisterCrashTelemetry();

        // Niet-blokkerende update-controle: bij een nieuwe versie krijgt de
        // gebruiker een prompt. Faalt stil bij geen internet / geen feed.
        _ = CheckForUpdatesOnStartupAsync();
    }

    private void RegisterCrashTelemetry()
    {
        if (_serviceProvider is null) return;
        var telemetry = _serviceProvider.GetRequiredService<ITelemetryService>();
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            SendCrashTelemetry(telemetry, "unobserved_task_exception", args.Exception, "task_scheduler");
            args.SetObserved();
        };
    }

    private static void SendCrashTelemetry(ITelemetryService telemetry, string eventName, Exception? exception, string source)
    {
        try
        {
            var task = telemetry.TrackEventAsync(eventName, new Dictionary<string, object?>
            {
                ["source"] = source,
                ["exception_type"] = exception?.GetType().Name ?? "unknown",
                ["stack_present"] = !string.IsNullOrWhiteSpace(exception?.StackTrace),
                ["process_terminating"] = eventName == "app_unhandled_exception"
            });
            task.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* telemetry must never prevent shutdown */ }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_serviceProvider is null)
        {
            return;
        }

        var updateService = _serviceProvider.GetRequiredService<IUpdateService>();
        var telemetryService = _serviceProvider.GetRequiredService<ITelemetryService>();
        if (!updateService.IsInstalled)
        {
            await telemetryService.TrackEventAsync("update_check_skipped", new Dictionary<string, object?>
            {
                ["source"] = "startup",
                ["reason"] = "not_installed"
            });
            return;
        }

        var result = await updateService.CheckForUpdatesAsync();
        await telemetryService.TrackEventAsync("update_check_completed", new Dictionary<string, object?>
        {
            ["source"] = "startup",
            ["update_available"] = result.UpdateAvailable,
            ["has_error"] = result.Error is not null,
            ["current_version"] = updateService.CurrentVersion,
            ["new_version"] = result.UpdateAvailable ? result.NewVersion : null
        });
        if (!result.UpdateAvailable)
        {
            return;
        }

        var dialogs = _serviceProvider.GetRequiredService<IMessageDialogService>();
        var confirmed = dialogs.Confirm(
            "Update beschikbaar",
            $"Versie {result.NewVersion} is beschikbaar (huidige versie {updateService.CurrentVersion}).\n\n" +
            "Nu downloaden en de app herstarten?");
        if (!confirmed)
        {
            await telemetryService.TrackEventAsync("update_prompt_declined", new Dictionary<string, object?>
            {
                ["source"] = "startup",
                ["new_version"] = result.NewVersion
            });
            return;
        }

        try
        {
            await updateService.DownloadAndApplyAsync();
        }
        catch (Exception ex)
        {
            dialogs.ShowError("Update mislukt", ex.Message);
            await telemetryService.TrackEventAsync("update_apply_failed", new Dictionary<string, object?>
            {
                ["source"] = "startup",
                ["exception_type"] = ex.GetType().Name
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddDebug();
        });

        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IMessageDialogService, MessageDialogService>();
        services.AddSingleton<IImportRepairWizardService, ImportRepairWizardService>();
        services.AddSingleton<IOnlineLogService, OnlineLogService>();
        services.AddSingleton<IOnlineLogSelectionHistoryStore, OnlineLogSelectionHistoryStore>();
        services.AddSingleton<IOnlineLogDialogService, OnlineLogDialogService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.AddSingleton<ITelemetryService, TelemetryService>();
        services.AddSingleton<IPlotModelBuilder, PlotModelBuilder>();
        services.AddSingleton<IXAxisSyncService, XAxisSyncService>();
        services.AddSingleton<IPlotWindowService, PlotWindowService>();

        services.AddSingleton<CssSemicolonParser>();
        services.AddSingleton<BusmasterParser>();
        services.AddSingleton<PeakTrcParser>();
        services.AddSingleton<IMdf4ConversionService, Mdf4ConversionService>();
        services.AddSingleton<Mdf4Parser>();
        services.AddSingleton<CandumpParser>();
        services.AddSingleton<GenericTextCanParser>();
        services.AddSingleton<ICanLogParsingService, CanLogParsingService>();

        services.AddSingleton<IDbcLoader, DbcLoader>();
        services.AddSingleton<IDbcWriter, DbcWriter>();
        services.AddSingleton<ICanDecodingService, CanDecodingService>();
        services.AddSingleton<IDatasetBuilder, DatasetBuilder>();
        services.AddSingleton<IRawFrameFilterService, RawFrameFilterService>();
        services.AddSingleton<IJoystickAnalyticsService, JoystickAnalyticsService>();
        services.AddSingleton<IActuatorCsvImportService, ActuatorCsvImportService>();
        services.AddSingleton<IImportRepairService, ImportRepairService>();
        services.AddSingleton<ICsvExportService, CsvExportService>();
        services.AddSingleton<IPresetSerializer, PresetSerializer>();
        services.AddSingleton<ICanAnalysisPipeline, CanAnalysisPipeline>();

        services.AddSingleton<ActiveUsageViewModel>();
        services.AddSingleton<AnalysisViewModel>();
        services.AddSingleton<JoystickAnalyticsViewModel>();
        services.AddSingleton<RawFramesViewModel>();
        services.AddSingleton<BusmasterViewModel>();
        services.AddSingleton<SettingsDiagnosticsViewModel>();
        services.AddSingleton<DbcEditorViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
