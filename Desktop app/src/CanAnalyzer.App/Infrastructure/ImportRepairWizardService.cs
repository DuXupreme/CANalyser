using System.Windows;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.ViewModels;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CanAnalyzer.App.Infrastructure;

public sealed class ImportRepairWizardService(
    IImportRepairService repairService,
    IServiceProvider serviceProvider) : IImportRepairWizardService
{
    public async Task<ImportRepairWizardResult> ShowAsync(
        ImportReport report,
        string logPath,
        string dbcPath,
        CancellationToken cancellationToken)
    {
        var analysis = await Task.Run(
            () => ImportRepairWizardWindow.AnalyzeReport(report, repairService),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var dbcEditor = ActivatorUtilities.CreateInstance<DbcEditorViewModel>(serviceProvider);
        var window = new ImportRepairWizardWindow(
            report,
            logPath,
            dbcPath,
            repairService,
            dbcEditor,
            analysis);
        Window? owner = null;
        if (Application.Current.MainWindow is { IsVisible: true } visibleOwner)
        {
            owner = visibleOwner;
            window.Owner = owner;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => completion.TrySetResult();
        try
        {
            if (owner is not null)
            {
                owner.IsEnabled = false;
            }

            window.Show();
            window.Activate();
            using var registration = cancellationToken.Register(() =>
                window.Dispatcher.BeginInvoke(new Action(window.Close)));
            await completion.Task;
        }
        finally
        {
            if (owner is not null)
            {
                owner.IsEnabled = true;
                owner.Activate();
            }
        }
        return window.Result;
    }
}
