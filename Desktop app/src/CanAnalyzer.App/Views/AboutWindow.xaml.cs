using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using CanAnalyzer.App.ViewModels;

namespace CanAnalyzer.App.Views;

/// <summary>
/// "Over"-venster met versie, auteur, updatecontrole en documentatielinks.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow(SettingsDiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        VersionText.Text = $"Versie {viewModel.AppVersion}";
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        // UseShellExecute opent de standaardbrowser voor een http(s)-URL.
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
