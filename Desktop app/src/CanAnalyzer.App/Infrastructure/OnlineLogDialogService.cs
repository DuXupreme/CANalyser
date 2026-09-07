using System.Windows;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;

namespace CanAnalyzer.App.Infrastructure;

public sealed class OnlineLogDialogService(
    IOnlineLogService onlineLogService,
    IOnlineLogSelectionHistoryStore historyStore) : IOnlineLogDialogService
{
    public string? SelectAndDownload()
    {
        var window = new OnlineLogsWindow(onlineLogService, historyStore)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.DownloadedArchivePath : null;
    }
}
