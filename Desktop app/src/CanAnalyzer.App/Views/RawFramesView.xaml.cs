using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CanAnalyzer.App.Models;
using CanAnalyzer.App.ViewModels;

namespace CanAnalyzer.App.Views;

public partial class RawFramesView : UserControl
{
    public RawFramesView()
    {
        InitializeComponent();
    }

    private void OnRawFrameDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not DataGrid grid || grid.SelectedItem is not RawFrameRow row)
            {
                return;
            }

            var text = DataContext is RawFramesViewModel viewModel
                ? viewModel.BuildFrameDetailsText(row)
                : $"Tijd [s]: {row.TimeSeconds:G17}\nTijd [ns]: {row.TimestampNanoseconds}\nFrame-index: {row.FrameIndex}\nBronregel: {row.SourceLineNumber}\nID: {row.IdHex}\nData: {row.DataHex}";

            MessageBox.Show(text, "Raw Frame details + decoded signals", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Dubbelklik details konden niet worden geopend:\n{ex.Message}",
                "Raw Frame fout",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
