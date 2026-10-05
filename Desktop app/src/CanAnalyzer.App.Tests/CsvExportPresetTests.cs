using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CanAnalyzer.App.State;
using CanAnalyzer.App.Views;
using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using CanAnalyzer.Core.Export;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class CsvExportPresetTests
{
    private static readonly SignalIdentity A = new("can1", CanFrameFormat.Classic, false, 0x123, "Parker", "Position");
    private static readonly SignalIdentity B = A with { Channel = "can2" };

    private static CanDataset Dataset(params SignalIdentity[] signals) => new DatasetBuilder().Build([], signals
        .Select((s, i) => new DecodedSignalSample(1000L * (i + 1), -1, -1, s, 12.345 + i, 12 + i, "mm")).ToArray(),
        [], new DecoderDiagnostics(0, 0, signals.Length, 0, 0, ""));

    [Fact]
    public Task PresetRoundTrip_RestoresExactSignalsColumnOrderAndFormatting() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var dataset = Dataset(A, B);
        string? saved = null;
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Task Save(IReadOnlyList<CsvExportPreset> presets)
        {
            saved = JsonSerializer.Serialize(new AppSettings { CsvExportPresets = presets.ToList() }, jsonOptions);
            return Task.CompletedTask;
        }
        var dialog = new CsvExportDialog(dataset, [], [], Save);
        try
        {
            await Show(dialog);
            dialog.Signals.Single(s => s.Value == B).IsSelected = false;
            foreach (var column in dialog.Columns) column.IsSelected = column.Value is "value" or "signal_name" or "time_s";
            dialog.Columns.Move(dialog.Columns.ToList().FindIndex(c => c.Value == "value"), 0);
            dialog.Columns.Move(dialog.Columns.ToList().FindIndex(c => c.Value == "signal_name"), 1);
            ((TextBox)dialog.FindName("PresetName")).Text = "Motorpositie";
            ((ComboBox)dialog.FindName("Delimiter")).SelectedIndex = 1;
            ((ComboBox)dialog.FindName("Decimals")).SelectedIndex = 1;
            ((ComboBox)dialog.FindName("TimeMode")).SelectedIndex = 2;
            ((CheckBox)dialog.FindName("Utf8Bom")).IsChecked = true;
            Assert.True(await dialog.SavePresetAsync(false));
            Assert.False(await dialog.SavePresetAsync(false)); // Duplicate names never silently replace a preset.
        }
        finally { dialog.Close(); }

        var restored = JsonSerializer.Deserialize<AppSettings>(saved!, jsonOptions)!;
        var reopened = new CsvExportDialog(dataset, [], restored.CsvExportPresets, Save);
        try
        {
            await Show(reopened);
            ((ComboBox)reopened.FindName("PresetList")).SelectedIndex = 0;
            var options = reopened.CaptureOptions();
            Assert.Equal(A, Assert.Single(options.Signals!));
            Assert.Equal(new[] { "value", "signal_name", "time_s" }, options.Columns);
            Assert.Equal(CsvTimeOrigin.FirstExportedSample, options.TimeOrigin);
            Assert.Equal(";", options.Delimiter); Assert.Equal(",", options.DecimalSeparator); Assert.True(options.IncludeUtf8Bom);
            var csv = await CsvExportService.PreviewAsync(dataset, options, CancellationToken.None);
            Assert.StartsWith("value;signal_name;time_s", csv);
            var row = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r').Split(';');
            Assert.Equal(12.345, double.Parse(row[0], System.Globalization.CultureInfo.GetCultureInfo("nl-NL")));
            Assert.Equal("Position", row[1]); Assert.Equal("0", row[2]);
            ((TextBox)reopened.FindName("PresetName")).Text = "Positie nieuw";
            reopened.Columns.Move(0, 2);
            Assert.True(await reopened.SavePresetAsync(true));
            var updated = Assert.Single(JsonSerializer.Deserialize<AppSettings>(saved!, jsonOptions)!.CsvExportPresets);
            Assert.Equal(restored.CsvExportPresets[0].Id, updated.Id);
            Assert.Equal("Positie nieuw", updated.Name);
            Assert.Equal(new[] { "signal_name", "time_s", "value" }, updated.Options.Columns);
            Assert.True(await reopened.DeletePresetAsync(updated.Id));
            Assert.Empty(JsonSerializer.Deserialize<AppSettings>(saved!, jsonOptions)!.CsvExportPresets);
            Assert.Equal(A, Assert.Single(reopened.CaptureOptions().Signals!));
        }
        finally { reopened.Close(); }
    });

    [Fact]
    public Task MissingSignals_AreReportedNotSubstitutedAndRemainInUpdatedPreset() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var dataset = Dataset(A);
        var preset = new CsvExportPreset { Name = "Beide kanalen", Options = new() { Signals = [A, B], Columns = ["value"] } };
        IReadOnlyList<CsvExportPreset>? saved = null;
        var dialog = new CsvExportDialog(dataset, [], [preset], items => { saved = items; return Task.CompletedTask; });
        try
        {
            await Show(dialog);
            ((ComboBox)dialog.FindName("PresetList")).SelectedIndex = 0;
            Assert.Equal(A, Assert.Single(dialog.CaptureOptions().Signals!));
            Assert.Contains("ontbreken", ((TextBlock)dialog.FindName("PresetStatus")).Text);
            Assert.Contains(B.DisplayLabel, (string)((TextBlock)dialog.FindName("PresetStatus")).ToolTip);
            Assert.True(await dialog.SavePresetAsync(true));
            Assert.Contains(B, saved![0].Options.Signals!);
            dialog.ApplyPreset(preset with { Options = preset.Options with { Signals = [B] } });
            Assert.Empty(dialog.CaptureOptions().Signals!);
            Assert.False(((Button)dialog.FindName("ExportButton")).IsEnabled);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task SaveFailure_LeavesPresetCollectionUnchanged() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var dataset = Dataset(A);
        var preset = new CsvExportPreset { Name = "Bestaand" };
        var dialog = new CsvExportDialog(dataset, [], [preset], _ => throw new System.IO.IOException("Testfout"));
        try
        {
            await Show(dialog);
            ((ComboBox)dialog.FindName("PresetList")).SelectedIndex = 0;
            ((TextBox)dialog.FindName("PresetName")).Text = "Gewijzigd";
            Assert.False(await dialog.SavePresetAsync(true));
            Assert.Same(preset, Assert.Single(dialog.Presets));
            Assert.Contains("Testfout", ((TextBlock)dialog.FindName("PresetStatus")).Text);
            Assert.True(((Button)dialog.FindName("UpdatePresetButton")).IsEnabled);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task ColumnArrow_ChangesPreviewOrderAndInvalidatesOldPreview() => PlotRegressionTests.OnDispatcher(async () =>
    {
        using var dataset = Dataset(A);
        var dialog = new CsvExportDialog(dataset, [])
        { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = (ListBox)dialog.FindName("ColumnList");
            var column = dialog.Columns[1];
            var up = Descendants(list).OfType<Button>().Single(b => Equals(b.Content, "↑") && ReferenceEquals(b.Tag, column));
            ((TextBox)dialog.FindName("PreviewText")).Text = "Oud voorbeeld";
            up.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Same(column, dialog.Columns[0]);
            Assert.Equal(column.Value, dialog.CaptureOptions().Columns[0]);
            Assert.Contains("Instellingen gewijzigd", ((TextBox)dialog.FindName("PreviewText")).Text);
            var csv = await CsvExportService.PreviewAsync(dataset, dialog.CaptureOptions(), CancellationToken.None);
            Assert.StartsWith(column.Value + ",time_s,", csv);
        }
        finally { dialog.Close(); }
    });

    private static async Task Show(CsvExportDialog dialog)
    {
        dialog.Left = -32000; dialog.Top = -32000;
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
        dialog.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
