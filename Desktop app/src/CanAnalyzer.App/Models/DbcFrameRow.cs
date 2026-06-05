using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Models;

/// <summary>
/// Editable frame (DBC message) row used by the database editor.
/// </summary>
public sealed partial class DbcFrameRow : ObservableObject
{
    [ObservableProperty]
    private string _name = "NewMessage";

    [ObservableProperty]
    private uint _frameId;

    [ObservableProperty]
    private bool _isExtended;

    [ObservableProperty]
    private int _dlc = 8;

    public DbcFrameRow()
    {
        Signals.CollectionChanged += OnSignalsChanged;
    }

    /// <summary>Signals contained in this frame.</summary>
    public ObservableCollection<DbcSignalRow> Signals { get; } = [];

    /// <summary>Number of signals (column in the frame list).</summary>
    public int SignalCount => Signals.Count;

    /// <summary>Hex display of the (normalized) CAN id, e.g. 0x18FF50E5.</summary>
    public string FrameIdHex => "0x" + FrameId.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Editable id text that accepts hex ("0x..") or decimal.</summary>
    public string FrameIdText
    {
        get => FrameIdHex;
        set
        {
            if (TryParseId(value, out var id))
            {
                FrameId = id;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Parses a CAN id written as hex ("0x123") or decimal ("291").</summary>
    public static bool TryParseId(string? text, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        try
        {
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                id = Convert.ToUInt32(trimmed[2..], 16);
                return true;
            }

            return uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }
        catch
        {
            return false;
        }
    }

    private void OnSignalsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(SignalCount));

    partial void OnFrameIdChanged(uint value)
    {
        OnPropertyChanged(nameof(FrameIdHex));
        OnPropertyChanged(nameof(FrameIdText));
    }
}
