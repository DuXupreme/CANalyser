using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CanAnalyzer.App.ViewModels;

namespace CanAnalyzer.App.Views;

/// <summary>
/// Database (DBC) editor tab view.
/// </summary>
public partial class DbcEditorView : UserControl
{
    public static readonly DependencyProperty FileToolbarVisibilityProperty =
        DependencyProperty.Register(
            nameof(FileToolbarVisibility),
            typeof(Visibility),
            typeof(DbcEditorView),
            new PropertyMetadata(Visibility.Visible));

    public DbcEditorView()
    {
        InitializeComponent();
    }

    public Visibility FileToolbarVisibility
    {
        get => (Visibility)GetValue(FileToolbarVisibilityProperty);
        set => SetValue(FileToolbarVisibilityProperty, value);
    }

    public void RevealSelection()
    {
        if (DataContext is not DbcEditorViewModel viewModel)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (viewModel.SelectedFrame is not null)
            {
                FramesGrid.ScrollIntoView(viewModel.SelectedFrame);
            }

            if (viewModel.SelectedSignal is not null)
            {
                SignalsGrid.ScrollIntoView(viewModel.SelectedSignal);
            }
        }, DispatcherPriority.Loaded);
    }

    public void SetBitLayoutExpanded(bool expanded)
    {
        BitLayoutExpander.IsExpanded = expanded;
    }
}
