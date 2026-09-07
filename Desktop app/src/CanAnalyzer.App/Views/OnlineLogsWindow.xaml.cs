using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Views;

public partial class OnlineLogsWindow : Window
{
    private readonly IOnlineLogService _onlineLogService;
    private readonly IOnlineLogSelectionHistoryStore _historyStore;
    private readonly CancellationTokenSource _windowCts = new();
    private int _maximumSelection = 200;
    private bool _isTruncated;
    private int _unknownRecordingTimes;
    private HashSet<string>? _keysToRestore;
    private string? _restoreNotice;

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
        Loaded += OnLoaded;
        Closed += (_, _) => _windowCts.Cancel();
    }

    public ObservableCollection<OnlineLogRow> Rows { get; } = [];
    public string? DownloadedArchivePath { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_keysToRestore is null) _restoreNotice = null;
        if (MachineBox.SelectedValue is not string loggerId || FromPicker.SelectedDate is not DateTime from || ToPicker.SelectedDate is not DateTime to)
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
        try
        {
            var fromUtc = new DateTimeOffset(from.Date).ToUniversalTime();
            var toUtc = new DateTimeOffset(to.Date.AddDays(1)).ToUniversalTime();
            var result = await _onlineLogService.GetLogsAsync(loggerId, fromUtc, toUtc, _windowCts.Token);
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
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Rows.Clear();
            _isTruncated = false;
            _unknownRecordingTimes = 0;
            StatusText.Text = "Online logs konden niet worden opgehaald.";
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

        if (MachineBox.SelectedValue is string loggerId && FromPicker.SelectedDate is DateTime from && ToPicker.SelectedDate is DateTime to)
        {
            var machineName = (MachineBox.SelectedItem as OnlineMachine)?.Name ?? OnlineMachineCatalog.ResolveName(loggerId);
            try
            {
                await _historyStore.RememberAsync(new OnlineLogSelectionHistoryEntry(
                    loggerId,
                    machineName,
                    from.Date,
                    to.Date,
                    DateTimeOffset.UtcNow,
                    selections), CancellationToken.None);
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
            DownloadedArchivePath = await _onlineLogService.DownloadArchiveAsync(
                selections,
                progress,
                _windowCts.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Download mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false);
            UpdateSelectionStatus();
        }
    }

    private void OnSelectSessionClick(object sender, RoutedEventArgs e) => SelectSession(true);

    private async void OnRestoreSelectionClick(object sender, RoutedEventArgs e)
    {
        if (RecentSelectionBox.SelectedItem is not OnlineLogSelectionHistoryEntry entry) return;
        MachineBox.SelectedValue = entry.LoggerId;
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
        DownloadButton.IsEnabled = withinMaximum && validation.IsValid;
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
        RecentSelectionBox.IsEnabled = !busy && RecentSelectionBox.Items.Count > 0;
        RestoreSelectionButton.IsEnabled = !busy && RecentSelectionBox.Items.Count > 0;
        LogsGrid.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:N1} MB"
        : $"{bytes / 1024d:N0} kB";

}
