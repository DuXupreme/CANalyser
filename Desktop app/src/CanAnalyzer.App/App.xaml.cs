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

namespace CanAnalyzer.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        _serviceProvider = BuildServiceProvider();
        var window = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = window;

        void CloseSplash(object? sender, EventArgs args)
        {
            window.ContentRendered -= CloseSplash;
            splash.Close();
        }

        window.ContentRendered += CloseSplash;
        window.Show();

        // Niet-blokkerende update-controle: bij een nieuwe versie krijgt de
        // gebruiker een prompt. Faalt stil bij geen internet / geen feed.
        _ = CheckForUpdatesOnStartupAsync();
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_serviceProvider is null)
        {
            return;
        }

        var updateService = _serviceProvider.GetRequiredService<IUpdateService>();
        if (!updateService.IsInstalled)
        {
            return;
        }

        var result = await updateService.CheckForUpdatesAsync();
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
            return;
        }

        try
        {
            await updateService.DownloadAndApplyAsync();
        }
        catch (Exception ex)
        {
            dialogs.ShowError("Update mislukt", ex.Message);
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
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.AddSingleton<IPlotModelBuilder, PlotModelBuilder>();
        services.AddSingleton<IXAxisSyncService, XAxisSyncService>();
        services.AddSingleton<IPlotWindowService, PlotWindowService>();

        services.AddSingleton<CssSemicolonParser>();
        services.AddSingleton<BusmasterParser>();
        services.AddSingleton<PeakTrcParser>();
        services.AddSingleton<CandumpParser>();
        services.AddSingleton<GenericTextCanParser>();
        services.AddSingleton<ICanLogParsingService, CanLogParsingService>();

        services.AddSingleton<IDbcLoader, DbcLoader>();
        services.AddSingleton<IDbcWriter, DbcWriter>();
        services.AddSingleton<ICanDecodingService, CanDecodingService>();
        services.AddSingleton<IDatasetBuilder, DatasetBuilder>();
        services.AddSingleton<IRawFrameFilterService, RawFrameFilterService>();
        services.AddSingleton<IJoystickAnalyticsService, JoystickAnalyticsService>();
        services.AddSingleton<ICsvExportService, CsvExportService>();
        services.AddSingleton<IPresetSerializer, PresetSerializer>();
        services.AddSingleton<ICanAnalysisPipeline, CanAnalysisPipeline>();

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
