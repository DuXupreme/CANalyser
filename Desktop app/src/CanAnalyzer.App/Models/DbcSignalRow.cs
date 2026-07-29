using System.Globalization;
using CanAnalyzer.Core.Decoding;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Models;

/// <summary>
/// Editable signal row used by the DBC database editor.
/// </summary>
public sealed partial class DbcSignalRow : ObservableObject
{
    [ObservableProperty]
    private string _name = "NewSignal";

    [ObservableProperty]
    private int _startBit;

    [ObservableProperty]
    private int _length = 8;

    [ObservableProperty]
    private bool _littleEndian = true;

    [ObservableProperty]
    private bool _signed;

    [ObservableProperty]
    private double _scale = 1d;

    [ObservableProperty]
    private double _offset;

    [ObservableProperty]
    private double _minimum;

    [ObservableProperty]
    private double _maximum;

    [ObservableProperty]
    private string _unit = string.Empty;

    [ObservableProperty]
    private bool _isMultiplexerSwitch;

    [ObservableProperty]
    private int? _multiplexedValue;

    [ObservableProperty]
    private bool _isRepairTarget;

    [ObservableProperty]
    private string _repairHint = string.Empty;

    public DbcSignalValueKind ValueKind { get; set; }

    public IReadOnlyList<DbcMultiplexerRange> MultiplexerRanges { get; set; } = [];

    public string Tooltip
    {
        get
        {
            var endian = LittleEndian ? "Intel / little-endian" : "Motorola / big-endian";
            var mux = string.IsNullOrWhiteSpace(MuxText) ? "altijd actief" : $"mux {MuxText}";
            var details = $"{Name}: startbit {StartBit}, {Length} bit, {endian}, {mux}.";
            return string.IsNullOrWhiteSpace(RepairHint) ? details : $"{RepairHint}\n\n{details}";
        }
    }

    /// <summary>
    /// DBC-style multiplex token: empty = normal signal, "M" = multiplexer switch,
    /// an integer = multiplexed group (m&lt;n&gt;).
    /// </summary>
    public string MuxText
    {
        get
        {
            if (IsMultiplexerSwitch)
            {
                return "M";
            }

            return MultiplexedValue.HasValue
                ? MultiplexedValue.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Equals("M", StringComparison.OrdinalIgnoreCase))
            {
                IsMultiplexerSwitch = true;
                MultiplexedValue = null;
            }
            else if (int.TryParse(trimmed.TrimStart('m', 'M'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                IsMultiplexerSwitch = false;
                MultiplexedValue = parsed;
            }
            else
            {
                IsMultiplexerSwitch = false;
                MultiplexedValue = null;
            }

            OnPropertyChanged();
        }
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Tooltip));

    partial void OnStartBitChanged(int value) => OnPropertyChanged(nameof(Tooltip));

    partial void OnLengthChanged(int value) => OnPropertyChanged(nameof(Tooltip));

    partial void OnLittleEndianChanged(bool value) => OnPropertyChanged(nameof(Tooltip));

    partial void OnRepairHintChanged(string value) => OnPropertyChanged(nameof(Tooltip));

    partial void OnIsMultiplexerSwitchChanged(bool value)
    {
        OnPropertyChanged(nameof(MuxText));
        OnPropertyChanged(nameof(Tooltip));
    }

    partial void OnMultiplexedValueChanged(int? value)
    {
        OnPropertyChanged(nameof(MuxText));
        OnPropertyChanged(nameof(Tooltip));
    }
}
