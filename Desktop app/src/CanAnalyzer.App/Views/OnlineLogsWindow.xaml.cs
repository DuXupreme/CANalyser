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
    private readonly CancellationTokenSource _windowCts = new();
    private int _maximumSelection = 200;
    private bool _isTruncated;

    public OnlineLogsWindow(IOnlineLogService onlineLogService)
    {
        InitializeComponent();
        _onlineLogService = onlineLogService;
        DataContext = this;
        MachineBox.ItemsSource = new[]
        {
            new MachineOption("Vlindermachine 1", "48EDFD35"),
            new MachineOption("Vlindermachine 2", "22484AAA")
        };
        MachineBox.SelectedIndex = 0;
        FromPicker.SelectedDate = DateTime.Today.AddDays(-7);
        ToPicker.SelectedDate = DateTime.Today;
        Loaded += OnLoaded;
        Closed += (_, _) => _windowCts.Cancel();
    }

    public ObservableCollection<OnlineLogRow> Rows { get; } = [];
    public string? DownloadedArchivePath { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
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
            var newestFile = result.Files.OrderByDescending(static file => file.CreatedAt).FirstOrDefault();
            foreach (var file in result.Files)
            {
                var row = new OnlineLogRow
                {
                    IsSelected = newestFile is not null &&
                                 string.Equals(file.Logger, newestFile.Logger, StringComparison.Ordinal) &&
                                 string.Equals(file.Session, newestFile.Session, StringComparison.Ordinal),
                    Key = file.Key,
                    Name = file.Name,
                    Machine = file.Machine,
                    Logger = file.Logger,
                    Session = file.Session,
                    CreatedAt = file.CreatedAt,
                    SizeBytes = file.SizeBytes
                };
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
            }

        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Rows.Clear();
            _isTruncated = false;
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
            StatusText.Text = "Geen MF4-bestanden gevonden in deze uploadperiode.";
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
            var sessionText = selected.Length > 1 ? $" uit sessie {selected[0].Session}" : string.Empty;
            StatusText.Text = $"{selected.Length:N0} van {Rows.Count:N0} bestand(en){sessionText} geselecteerd, " +
                              $"{FormatBytes(selected.Sum(static row => row.SizeBytes))}." +
                              (_isTruncated ? " Er zijn meer resultaten; kies een kortere periode om alles te zien." : string.Empty);
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(94, 107, 117));
        }
        DownloadButton.IsEnabled = withinMaximum && validation.IsValid;
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
        LogsGrid.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:N1} MB"
        : $"{bytes / 1024d:N0} kB";

    private sealed record MachineOption(string Name, string LoggerId);
}
