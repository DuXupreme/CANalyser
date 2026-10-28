using System.Windows;
using System.Windows.Data;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.Services;

namespace CanAnalyzer.App.Views;

public partial class OnlineLogsWindow
{
    private string SelectedTimeBasis => TimeBasisBox.SelectedValue as string ?? "recording";

    private OnlineLogSelection[] CreateSelectionWithOmissions(IEnumerable<OnlineLogRow> selected) =>
        OnlineLogSelectionPolicy.IncludeMarkedBetweenSelections(CreateSelections(selected), CreateSelections(Rows));

    private void OnShowExcludedChanged(object sender, RoutedEventArgs e)
    {
        if (LogsGrid is null) return;
        LogsGrid.CommitEdit();
        CollectionViewSource.GetDefaultView(Rows).Refresh();
    }

    private async void OnMarkExcludedClick(object sender, RoutedEventArgs e) => await UpdateAnnotationsAsync(true);
    private async void OnUnmarkExcludedClick(object sender, RoutedEventArgs e) => await UpdateAnnotationsAsync(false);

    private async Task UpdateAnnotationsAsync(bool excluded)
    {
        LogsGrid.CommitEdit();
        var rows = LogsGrid.SelectedItems.OfType<OnlineLogRow>().ToArray();
        var reason = AnnotationReasonBox.Text.Trim();
        if (!_annotationsAvailable || rows.Length is < 1 or > 25 ||
            rows.Any(row => row.Provenance?.Annotation is null || string.IsNullOrWhiteSpace(row.Provenance.ObjectVersion)))
        {
            MessageBox.Show(this, "Selecteer 1 tot 25 tabelrijen met een bekende markering en bestandsversie. Vernieuw zo nodig de lijst.",
                "Gedeelde markering", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (excluded && string.IsNullOrWhiteSpace(reason))
        {
            MessageBox.Show(this, "Vul een reden in voor deze markering.", "Gedeelde markering", MessageBoxButton.OK, MessageBoxImage.Information);
            AnnotationReasonBox.Focus();
            return;
        }
        _keysToRestore = Rows.Where(row => row.IsSelected).Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        SetBusy(true, "Gedeelde markeringen opslaan...");
        try
        {
            var updates = rows.Select(row => new OnlineLogAnnotationUpdate(row.Key, row.Provenance!.ObjectVersion!,
                row.Provenance.Annotation!.Revision, excluded, excluded ? reason : "")).ToArray();
            await WithAuthenticationAsync(() => _onlineLogService.UpdateAnnotationsAsync(updates, _windowCts.Token));
            AnnotationReasonBox.Clear();
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested) { return; }
        catch (OnlineLogSignInCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message + "\nDe actuele markeringen worden opnieuw opgehaald.", "Markering niet opgeslagen",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { if (!_windowCts.IsCancellationRequested) SetBusy(false); }
        if (!_windowCts.IsCancellationRequested) await RefreshAsync();
    }
}
