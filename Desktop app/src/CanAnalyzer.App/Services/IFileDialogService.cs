namespace CanAnalyzer.App.Services;

/// <summary>
/// Wrapper around native file open/save dialogs.
/// </summary>
public interface IFileDialogService
{
    string? PickLogFile(string? initialPath);

    string? PickDbcFile(string? initialPath);

    string? SaveDbcFile(string? initialPath);

    string? PickPresetFile(string? initialPath);

    string? SavePresetFile(string? initialPath);

    string? SaveCsvFile(string? initialPath);
}
