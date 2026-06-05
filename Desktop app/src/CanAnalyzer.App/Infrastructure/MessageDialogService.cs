using System.Windows;
using CanAnalyzer.App.Services;

namespace CanAnalyzer.App.Infrastructure;

/// <inheritdoc />
public sealed class MessageDialogService : IMessageDialogService
{
    public void ShowInfo(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void ShowError(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
