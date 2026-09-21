using System.Windows.Controls;

namespace CanAnalyzer.App.Views;

public partial class SettingsDiagnosticsView : UserControl
{
    public SettingsDiagnosticsView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is CanAnalyzer.App.ViewModels.SettingsDiagnosticsViewModel model &&
                model.RefreshOnlineCacheCommand.CanExecute(null))
                await model.RefreshOnlineCacheCommand.ExecuteAsync(null);
        };
    }
}
