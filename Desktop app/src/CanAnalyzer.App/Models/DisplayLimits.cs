using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Models;

/// <summary>Shared display limits. These never limit the measurement or analysis input.</summary>
public sealed class DisplayLimits : ObservableObject
{
    private int _maxPointsPerTrace = 5000;
    private bool _useDownsampling = true;
    private int _rawRowsPerPage = 50_000;

    public int MaxPointsPerTrace
    {
        get => _maxPointsPerTrace;
        set => SetProperty(ref _maxPointsPerTrace, Math.Clamp(value, 200, 200_000));
    }

    public bool UseDownsampling
    {
        get => _useDownsampling;
        set => SetProperty(ref _useDownsampling, value);
    }

    public int RawRowsPerPage
    {
        get => _rawRowsPerPage;
        set => SetProperty(ref _rawRowsPerPage, Math.Clamp(value, 1, 2_000_000));
    }
}
