using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Views;

public partial class OnlineLogsWindow : Window
{
    private readonly IOnlineLogService _onlineLogService;
    private readonly IOnlineLogSelectionHistoryStore _historyStore;
    private readonly CancellationTokenSource _windowCts = new();
    private int _maximumSelection = Mdf4ImportLimits.MaximumFiles;
    private bool _isTruncated;
    private int _unknownRecordingTimes;
    private HashSet<string>? _keysToRestore;
    private string? _restoreNotice;
    private string? _loadError;

    public OnlineLogsWindow(IOnlineLogService onlineLogService, IOnlineLogSelectionHistoryStore historyStore)
    {
        InitializeComponent();
        _onlineLogService = onlineLogService;
        _historyStore = historyStore;
        DataContext = this;
        MachineBox.ItemsSource = OnlineMachineCatalog.Machines;
        MachineBox.SelectedIndex = 0;
        FromPicker.SelectedDate = DateTime.Today.AddDays(-7);
        ToPicker.SelectedDate = DateTime.Today;
        ReloadHistory();
        UpdateAuthenticationStatus();
        Loaded += OnLoaded;
        Closed += (_, _) => _windowCts.Cancel();
    }

    public ObservableCollection<OnlineLogRow> Rows { get; } = [];
    public string? DownloadedArchivePath { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Finish WPF's Loaded event before a fast 401 response opens a modal login.
        // A nested dialog during the load broadcast can prevent its own Loaded event.
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (!_windowCts.IsCancellationRequested) await RefreshAsync();
    }
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_keysToRestore is null) _restoreNotice = null;
        if (MachineBox.SelectedValue is not string machineId || FromPicker.SelectedDate is not DateTime from || ToPicker.SelectedDate is not DateTime to)
        {
            MessageBox.Show(this, "Kies een machine en een geldige begin- en einddatum.", "Online logs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (from.Date > to.Date)
        {
            MessageBox.Show(this, "De begindatum ligt na de einddatum.", "Online logs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Online loglijst ophalen...");
        _loadError = null;
        try
        {
            var fromUtc = new DateTimeOffset(from.Date).ToUniversalTime();
            var toUtc = new DateTimeOffset(to.Date.AddDays(1)).ToUniversalTime();
            var result = await WithAuthenticationAsync(() =>
                _onlineLogService.GetLogsAsync(machineId, fromUtc, toUtc, _windowCts.Token));
            Rows.Clear();
            _maximumSelection = result.MaximumSelection;
            _isTruncated = result.Truncated;
            _unknownRecordingTimes = result.UnknownRecordingTimes;
            var newestFile = result.Files.Where(static file => file.RecordedAt.HasValue).OrderByDescending(static file => file.RecordedAt).FirstOrDefault();
            var restoreKeys = _keysToRestore;
            foreach (var file in result.Files)
            {
                var row = new OnlineLogRow
                {
                    IsSelected = restoreKeys is not null
                        ? restoreKeys.Contains(file.Key)
                        : newestFile is not null &&
                          string.Equals(file.Logger, newestFile.Logger, StringComparison.Ordinal) &&
                          string.Equals(file.Session, newestFile.Session, StringComparison.Ordinal),
                    Key = file.Key,
                    Name = file.Name,
                    Machine = file.Machine,
                    Logger = file.Logger,
                    Session = file.Session,
                    RecordedAt = file.RecordedAt,
                    UploadedAt = file.UploadedAt,
                    SizeBytes = file.SizeBytes
                };
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
            }

            if (restoreKeys is not null)
            {
                var restored = Rows.Count(row => row.IsSelected);
                _restoreNotice = restored == restoreKeys.Count
                    ? $"Recente selectie hersteld: {restored:N0} bestand(en)."
                    : $"Recente selectie deels hersteld: {restored:N0} van {restoreKeys.Count:N0} bestand(en) zijn nog online beschikbaar.";
                _keysToRestore = null;
            }

        }
        catch (OnlineLogSignInCanceledException)
        {
            Rows.Clear();
            _isTruncated = false;
            _unknownRecordingTimes = 0;
            _loadError = "Aanmelden geannuleerd. Klik op Vernieuwen om opnieuw aan te melden.";
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Rows.Clear();
            _isTruncated = false;
            _unknownRecordingTimes = 0;
            _loadError = "Online logs konden niet worden opgehaald. Klik op Vernieuwen om opnieuw te proberen.";
            MessageBox.Show(this, ex.Message, "Online logs ophalen mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateSelectionStatus();
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        LogsGrid.CommitEdit();
        var selected = Rows.Where(static row => row.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Selecteer minimaal één MF4-bestand.", "Online logs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (selected.Length > _maximumSelection)
        {
            MessageBox.Show(this, $"Selecteer maximaal {_maximumSelection:N0} bestanden per analyse.", "Online logs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selections = CreateSelections(selected);
        var validation = ValidateSelection(selections);
        if (!validation.IsValid)
        {
            MessageBox.Show(this, validation.Message, "Deze bestanden kunnen niet samen", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateSelectionStatus();
            return;
        }

        if (MachineBox.SelectedItem is OnlineMachine machine && FromPicker.SelectedDate is DateTime from && ToPicker.SelectedDate is DateTime to)
        {
            try
            {
                await _historyStore.RememberAsync(new OnlineLogSelectionHistoryEntry(
                    machine.LoggerId,
                    machine.Name,
                    from.Date,
                    to.Date,
                    DateTimeOffset.UtcNow,
                    selections) { MachineId = machine.MachineId }, CancellationToken.None);
                ReloadHistory();
            }
            catch (Exception)
            {
                // History is a convenience; a write failure must never block the requested download.
            }
        }

        SetBusy(true, $"{selected.Length:N0} bestand(en) downloaden...");
        try
        {
            var progress = new Progress<OnlineDownloadProgress>(value =>
            {
                StatusText.Text = value.TotalBytes is > 0
                    ? $"Downloaden: {FormatBytes(value.BytesReceived)} van {FormatBytes(value.TotalBytes.Value)}"
                    : $"Downloaden: {FormatBytes(value.BytesReceived)}";
            });
            DownloadedArchivePath = await WithAuthenticationAsync(() => _onlineLogService.DownloadArchiveAsync(
                selections,
                progress,
                _windowCts.Token));
            DialogResult = true;
        }
        catch (OnlineLogSignInCanceledException)
        {
            SetBusy(false);
            UpdateSelectionStatus();
            StatusText.Text = "Aanmelden geannuleerd. Je selectie blijft behouden; klik op Downloaden en analyseren om opnieuw te proberen.";
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Download mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false);
            UpdateSelectionStatus();
            StatusText.Text = "Download mislukt. Je selectie blijft behouden; probeer opnieuw.";
            StatusText.Foreground = Brushes.Firebrick;
        }
    }

    private void OnSelectSessionClick(object sender, RoutedEventArgs e) => SelectSession(true);

    private async void OnRestoreSelectionClick(object sender, RoutedEventArgs e)
    {
        if (RecentSelectionBox.SelectedItem is not OnlineLogSelectionHistoryEntry entry) return;
        MachineBox.SelectedValue = entry.EffectiveMachineId;
        FromPicker.SelectedDate = entry.FromDate.Date;
        ToPicker.SelectedDate = entry.ToDate.Date;
        _keysToRestore = entry.Files.Select(file => file.Key).ToHashSet(StringComparer.Ordinal);
        _restoreNotice = null;
        await RefreshAsync();
    }
    private void OnDeselectSessionClick(object sender, RoutedEventArgs e) => SelectSession(false);
    private void SelectSession(bool selected)
    {
        LogsGrid.CommitEdit();
        if (LogsGrid.SelectedItem is not OnlineLogRow active) return;
        foreach (var row in Rows.Where(row => row.Logger == active.Logger && row.Session == active.Session))
            row.IsSelected = selected;
        UpdateSelectionStatus();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) row.IsSelected = true;
        UpdateSelectionStatus();
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) row.IsSelected = false;
        UpdateSelectionStatus();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnlineLogRow.IsSelected)) UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus()
    {
        if (!IsLoaded || !RefreshButton.IsEnabled) return;
        var selected = Rows.Where(static row => row.IsSelected).ToArray();
        var validation = ValidateSelection(CreateSelections(selected));
        var withinMaximum = selected.Length <= _maximumSelection;
        var withinSize = selected.Sum(static row => row.SizeBytes) <= Mdf4ImportLimits.MaximumBytes &&
                         selected.All(static row => row.SizeBytes >= 0 && row.SizeBytes <= Mdf4ImportLimits.MaximumFileBytes);
        if (_loadError is not null)
        {
            StatusText.Text = _loadError;
            StatusText.Foreground = Brushes.Firebrick;
            DownloadButton.IsEnabled = false;
            return;
        }
        if (Rows.Count == 0)
        {
            StatusText.Text = "Geen MF4-bestanden gevonden met een meetstart in deze periode.";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(94, 107, 117));
        }
        else if (!withinMaximum)
        {
            StatusText.Text = $"Selecteer maximaal {_maximumSelection:N0} bestanden per analyse. Kies één sessie of een korter deel daarvan.";
            StatusText.Foreground = Brushes.Firebrick;
        }
        else if (!withinSize)
        {
            StatusText.Text = "De selectie is groter dan 4 GB of bevat een bestand groter dan 512 MB. Kies een kleinere periode.";
            StatusText.Foreground = Brushes.Firebrick;
        }
        else if (!validation.IsValid)
        {
            StatusText.Text = validation.Message;
            StatusText.Foreground = Brushes.Firebrick;
        }
        else
        {
            var sessionText = $" uit {selected.Select(row => (row.Logger, row.Session)).Distinct().Count()} sessie(s)";
            StatusText.Text = $"{selected.Length:N0} van {Rows.Count:N0} bestand(en){sessionText} geselecteerd, " +
                              $"{FormatBytes(selected.Sum(static row => row.SizeBytes))}." +
                              (_isTruncated ? " Er zijn meer resultaten; kies een kortere periode om alles te zien." : string.Empty);
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(94, 107, 117));
        }
        if (!string.IsNullOrWhiteSpace(_restoreNotice)) StatusText.Text = _restoreNotice + " " + StatusText.Text;
        if (_unknownRecordingTimes > 0)
            StatusText.Text += $" Van {_unknownRecordingTimes:N0} bestand(en) is de meettijd niet leesbaar; deze vallen buiten de datumselectie.";
        DownloadButton.IsEnabled = withinMaximum && withinSize && validation.IsValid;
    }

    private void ReloadHistory()
    {
        var entries = _historyStore.Load();
        RecentSelectionBox.ItemsSource = entries;
        RecentSelectionBox.SelectedIndex = entries.Count > 0 ? 0 : -1;
        RecentSelectionBox.IsEnabled = entries.Count > 0;
        RestoreSelectionButton.IsEnabled = entries.Count > 0;
        RecentSelectionHint.Text = entries.Count > 0
            ? "Je laatste vijf downloadselecties blijven bewaard na opnieuw starten."
            : "Nog geen eerdere online selectie opgeslagen.";
    }

    private static OnlineLogSelection[] CreateSelections(IEnumerable<OnlineLogRow> rows) => rows
        .Select(static row => new OnlineLogSelection(row.Key, row.Name, row.Logger, row.Session, row.SizeBytes))
        .ToArray();

    private static OnlineLogSequenceValidation ValidateSelection(IReadOnlyList<OnlineLogSelection> files) =>
        OnlineLogSequencePolicy.Validate(files
            .Select(static file => new OnlineLogPartIdentity(file.Logger, file.Session, file.Name))
            .ToArray());

    private void SetBusy(bool busy, string? status = null)
    {
        MachineBox.IsEnabled = !busy;
        FromPicker.IsEnabled = !busy;
        ToPicker.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        SignOutButton.IsEnabled = !busy;
        UpdateAuthenticationStatus();
        RecentSelectionBox.IsEnabled = !busy && RecentSelectionBox.Items.Count > 0;
        RestoreSelectionButton.IsEnabled = !busy && RecentSelectionBox.Items.Count > 0;
        LogsGrid.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private Task<T> WithAuthenticationAsync<T>(Func<Task<T>> operation) =>
        OnlineLogAuthenticationFlow.RunAsync(operation, credentialsRejected =>
        {
            var login = new OnlineLogLoginWindow(_onlineLogService, credentialsRejected) { Owner = this };
            var submitted = login.ShowDialog() == true;
            if (!submitted) _onlineLogService.ClearCredentials();
            UpdateAuthenticationStatus();
            return submitted;
        }, _windowCts.Token);

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        try { _onlineLogService.ClearCredentials(); }
        catch (OnlineLogCredentialStorageException ex)
        {
            MessageBox.Show(this, ex.Message, "Afmelden mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Rows.Clear();
        _isTruncated = false;
        _unknownRecordingTimes = 0;
        _restoreNotice = null;
        _loadError = "Je bent afgemeld. Klik op Vernieuwen om opnieuw aan te melden.";
        UpdateAuthenticationStatus();
        UpdateSelectionStatus();
    }

    private void UpdateAuthenticationStatus() => SignOutButton.Visibility =
        _onlineLogService.AuthenticationUserName is null ? Visibility.Collapsed : Visibility.Visible;

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:N1} MB"
        : $"{bytes / 1024d:N0} kB";

}
