using System.Windows;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
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
        _serviceProvider = BuildServiceProvider();
        var window = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
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
        services.AddSingleton<SettingsDiagnosticsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
