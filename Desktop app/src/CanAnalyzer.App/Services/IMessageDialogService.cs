namespace CanAnalyzer.App.Services;

/// <summary>
/// Wrapper around modal message dialogs.
/// </summary>
public interface IMessageDialogService
{
    void ShowInfo(string title, string message);

    void ShowError(string title, string message);

    /// <summary>Shows a Yes/No prompt. Returns true when the user confirms.</summary>
    bool Confirm(string title, string message);
}
