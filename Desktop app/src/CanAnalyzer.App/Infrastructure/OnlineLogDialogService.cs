using System.Windows;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;

namespace CanAnalyzer.App.Infrastructure;

public sealed class OnlineLogDialogService(IOnlineLogService onlineLogService) : IOnlineLogDialogService
{
    public string? SelectAndDownload()
    {
        var window = new OnlineLogsWindow(onlineLogService)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.DownloadedArchivePath : null;
    }
}
