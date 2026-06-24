using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace CanAnalyzer.App.Views;

/// <summary>
/// Eenvoudig "Over"-venster met versie, auteur en links naar de repo/documentatie.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "onbekend";
        VersionText.Text = $"Versie {version}";
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        // UseShellExecute opent de standaardbrowser voor een http(s)-URL.
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
