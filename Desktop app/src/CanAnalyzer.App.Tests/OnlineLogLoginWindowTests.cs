using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CanAnalyzer.App.Services;
using CanAnalyzer.App.Views;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class OnlineLogLoginWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RejectedCredentials_PrefillOnlyUserName_AndSubmissionPreservesPassword(bool remember) => PlotRegressionTests.OnDispatcher(() =>
    {
        var service = new LoginService { AuthenticationUserName = "dashboard-user" };
        var window = Offscreen(new OnlineLogLoginWindow(service, credentialsRejected: true));
        const string password = "  test-only:pássword  ";
        var interaction = OnDialogLoaded(window, () =>
        {
            Assert.Equal("dashboard-user", Named<TextBox>(window, "UsernameBox").Text);
            var input = Named<PasswordBox>(window, "PasswordInput");
            Assert.Empty(input.Password);
            Assert.NotEqual('\0', input.PasswordChar);
            var error = Named<TextBlock>(window, "ErrorText");
            Assert.Equal(Visibility.Visible, error.Visibility);
            Assert.Contains("geweigerd", error.Text);
            var rememberBox = Named<CheckBox>(window, "RememberLoginBox");
            Assert.False(rememberBox.IsChecked);
            rememberBox.IsChecked = remember;

            input.Password = password;
            SavePreviewIfRequested(window);
            Click(window, "Aanmelden");
        });

        Assert.True(window.ShowDialog());
        interaction.GetAwaiter().GetResult();
        Assert.Equal("dashboard-user", service.AuthenticationUserName);
        Assert.Equal(password, service.Password);
        Assert.Equal(1, service.AcceptedCredentials);
        Assert.Equal(remember, service.RememberCredentials);
        Assert.Empty(Named<PasswordBox>(window, "PasswordInput").Password);
        return Task.CompletedTask;
    });

    [Fact]
    public Task InvalidCredentials_KeepDialogOpen_WithoutAcceptingCredentials() => PlotRegressionTests.OnDispatcher(() =>
    {
        var service = new LoginService();
        var window = Offscreen(new OnlineLogLoginWindow(service, credentialsRejected: false));
        var interaction = OnDialogLoaded(window, () =>
        {
            var username = Named<TextBox>(window, "UsernameBox");
            var password = Named<PasswordBox>(window, "PasswordInput");
            username.Text = "   ";
            password.Password = "test-only";
            Click(window, "Aanmelden");
            Assert.True(window.IsVisible);
            Assert.Null(window.DialogResult);
            Assert.Equal(0, service.AcceptedCredentials);
            Assert.Null(service.AuthenticationUserName);
            Assert.Contains("gebruikersnaam", Named<TextBlock>(window, "ErrorText").Text);

            username.Text = "dashboard-user";
            password.Clear();
            Click(window, "Aanmelden");
            Assert.True(window.IsVisible);
            Assert.Null(window.DialogResult);
            Assert.Equal(0, service.AcceptedCredentials);
            Assert.Null(service.AuthenticationUserName);
            Assert.Equal(Visibility.Visible, Named<TextBlock>(window, "ErrorText").Visibility);
            Assert.Contains("wachtwoord", Named<TextBlock>(window, "ErrorText").Text);

            password.Password = "test-only-unsent";
            window.Close();
        });

        Assert.False(window.ShowDialog());
        interaction.GetAwaiter().GetResult();
        Assert.Empty(Named<PasswordBox>(window, "PasswordInput").Password);
        Assert.Null(service.Password);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancelledAuthentication_KeepsCancellationStatus_AndRefreshCanSignIn(bool asynchronousChallenge) => PlotRegressionTests.OnDispatcher(async () =>
    {
        var service = new LoginService { AsynchronousChallenge = asynchronousChallenge };
        var window = Offscreen(new OnlineLogsWindow(service, new EmptyHistoryStore()));
        try
        {
            var cancellation = OnOwnedLogin(window, login => login.Close());
            window.Show();
            await cancellation;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Empty(window.Rows);
            Assert.Contains("Aanmelden geannuleerd", Named<TextBlock>(window, "StatusText").Text);
            Assert.DoesNotContain("Geen MF4-bestanden", Named<TextBlock>(window, "StatusText").Text);
            Assert.True(Named<Button>(window, "RefreshButton").IsEnabled);
            Assert.False(Named<Button>(window, "DownloadButton").IsEnabled);
            Assert.Equal(1, service.ClearCredentialsCalls);

            var signIn = OnOwnedLogin(window, login =>
            {
                Named<TextBox>(login, "UsernameBox").Text = "dashboard-user";
                Named<PasswordBox>(login, "PasswordInput").Password = "test-only";
                Click(login, "Aanmelden");
            });
            Click(window, "Vernieuwen");
            await signIn;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Single(window.Rows);
            Assert.DoesNotContain("Aanmelden geannuleerd", Named<TextBlock>(window, "StatusText").Text);
            Assert.True(Named<Button>(window, "RefreshButton").IsEnabled);
            Assert.Equal(Visibility.Visible, Named<Button>(window, "SignOutButton").Visibility);
            Assert.Equal(1, service.AcceptedCredentials);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SignOut_ClearsCredentialsAndRows_AndLeavesRefreshAvailable() => PlotRegressionTests.OnDispatcher(async () =>
    {
        var service = new LoginService();
        service.SetCredentials("dashboard-user", "test-only");
        var window = Offscreen(new OnlineLogsWindow(service, new EmptyHistoryStore()));
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Single(window.Rows);
            Click(window, "Afmelden");

            Assert.Null(service.AuthenticationUserName);
            Assert.Null(service.Password);
            Assert.Equal(1, service.ClearCredentialsCalls);
            Assert.Empty(window.Rows);
            Assert.Contains("Je bent afgemeld", Named<TextBlock>(window, "StatusText").Text);
            Assert.Equal(Visibility.Collapsed, Named<Button>(window, "SignOutButton").Visibility);
            Assert.True(Named<Button>(window, "RefreshButton").IsEnabled);
            Assert.False(Named<Button>(window, "DownloadButton").IsEnabled);
        }
        finally { window.Close(); }
    });

    private static T Offscreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));

    private static void Click(Window window, string content) => Descendants(window).OfType<Button>()
        .Single(button => Equals(button.Content, content)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static Task OnDialogLoaded(Window window, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); window.Close(); }
        }));
        return completion.Task;
    }

    private static Task OnOwnedLogin(Window owner, Action<OnlineLogLoginWindow> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        timer.Tick += (_, _) =>
        {
            var login = owner.OwnedWindows.OfType<OnlineLogLoginWindow>().FirstOrDefault();
            if (login is null || !login.IsLoaded)
            {
                if (DateTime.UtcNow < deadline) return;
                timer.Stop();
                completion.TrySetException(new TimeoutException("The authentication dialog did not open."));
                return;
            }
            timer.Stop();
            try { Offscreen(login); action(login); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); login.Close(); }
        };
        timer.Start();
        return completion.Task;
    }

    private static void SavePreviewIfRequested(Window window)
    {
        var path = Environment.GetEnvironmentVariable("CANALYSER_LOGIN_PREVIEW_PATH");
        if (string.IsNullOrEmpty(path)) return;
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            drawing.DrawRectangle(window.Background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(root), null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class EmptyHistoryStore : IOnlineLogSelectionHistoryStore
    {
        public IReadOnlyList<OnlineLogSelectionHistoryEntry> Load() => [];
        public Task RememberAsync(OnlineLogSelectionHistoryEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LoginService : IOnlineLogService
    {
        public string? AuthenticationUserName { get; set; }
        public string? Password { get; private set; }
        public bool RememberCredentials { get; private set; }
        public bool AsynchronousChallenge { get; init; }
        public int AcceptedCredentials { get; private set; }
        public int ClearCredentialsCalls { get; private set; }

        public void SetCredentials(string username, string password, bool rememberFor30Days = false)
        {
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Vul een gebruikersnaam in.");
            if (password.Length == 0) throw new ArgumentException("Vul een wachtwoord in.");
            AuthenticationUserName = username.Trim();
            Password = password;
            RememberCredentials = rememberFor30Days;
            AcceptedCredentials++;
        }

        public void ClearCredentials()
        {
            AuthenticationUserName = null;
            Password = null;
            RememberCredentials = false;
            ClearCredentialsCalls++;
        }

        public Task<OnlineLogQueryResult> GetLogsAsync(string loggerId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
        {
            if (AuthenticationUserName is null)
            {
                if (AsynchronousChallenge) return ChallengeAsync();
                throw new OnlineLogAuthenticationException(credentialsRejected: false);
            }
            return Task.FromResult(new OnlineLogQueryResult(
                [new("logger/session/00000001.MF4", "00000001.MF4", "Test machine", loggerId, "session", fromUtc, fromUtc, 100)],
                false, 100, 0));
        }

        private static async Task<OnlineLogQueryResult> ChallengeAsync()
        {
            await Task.Yield();
            throw new OnlineLogAuthenticationException(credentialsRejected: false);
        }

        public Task<string> DownloadArchiveAsync(IReadOnlyList<OnlineLogSelection> files, IProgress<OnlineDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
