using System.Windows;
using CanAnalyzer.App.Services;

namespace CanAnalyzer.App.Views;

public partial class OnlineLogLoginWindow : Window
{
    private readonly IOnlineLogService _onlineLogService;

    public OnlineLogLoginWindow(IOnlineLogService onlineLogService, bool credentialsRejected)
    {
        InitializeComponent();
        _onlineLogService = onlineLogService;
        UsernameBox.Text = onlineLogService.AuthenticationUserName ?? string.Empty;
        RememberLoginBox.IsChecked = onlineLogService.RememberCredentials;
        if (credentialsRejected)
        {
            ErrorText.Text = "Het dashboard heeft deze aanmelding geweigerd. Controleer je gebruikersnaam en wachtwoord.";
            ErrorText.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) =>
        {
            if (UsernameBox.Text.Length == 0) UsernameBox.Focus();
            else PasswordInput.Focus();
        };
        Closed += (_, _) => PasswordInput.Clear();
    }

    private void OnSignInClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _onlineLogService.SetCredentials(UsernameBox.Text, PasswordInput.Password, RememberLoginBox.IsChecked == true);
            PasswordInput.Clear();
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        catch (OnlineLogCredentialStorageException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
