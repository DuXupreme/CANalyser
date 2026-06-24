using System.Windows;

namespace CanAnalyzer.App.Views;

/// <summary>
/// Branded opstartvenster dat zichtbaar blijft totdat het hoofdvenster is gerenderd.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>
    /// Initialiseert de opstartsplash.
    /// </summary>
    public SplashWindow()
    {
        InitializeComponent();
    }
}
