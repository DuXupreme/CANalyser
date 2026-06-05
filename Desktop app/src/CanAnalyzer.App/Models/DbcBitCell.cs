using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Models;

/// <summary>
/// One cell in the 8x8 payload bit-layout grid. Rows are bytes 0..7 (top to bottom),
/// columns are bit 7..0 (MSB on the left), matching the conventional DBC layout view.
/// </summary>
public sealed partial class DbcBitCell : ObservableObject
{
    public DbcBitCell(int byteIndex, int column)
    {
        ByteIndex = byteIndex;
        Column = column;
        Lsb0Index = (byteIndex * 8) + (7 - column);
    }

    /// <summary>Byte (row) index 0..7.</summary>
    public int ByteIndex { get; }

    /// <summary>Column 0..7 (0 = MSB / bit 7, 7 = LSB / bit 0).</summary>
    public int Column { get; }

    /// <summary>Flat LSB0 bit index (byte * 8 + bit-in-byte).</summary>
    public int Lsb0Index { get; }

    /// <summary>Label shown inside the cell (the LSB0 bit number).</summary>
    public string BitLabel => Lsb0Index.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private Brush _fillBrush = Brushes.WhiteSmoke;

    [ObservableProperty]
    private string _ownerLabel = string.Empty;

    [ObservableProperty]
    private bool _isOverlap;

    /// <summary>Hover text describing which signal (if any) owns the bit.</summary>
    public string Tooltip => string.IsNullOrEmpty(OwnerLabel)
        ? $"bit {Lsb0Index} (byte {ByteIndex}.{7 - Column}) — vrij"
        : $"bit {Lsb0Index} (byte {ByteIndex}.{7 - Column}) — {OwnerLabel}";

    partial void OnOwnerLabelChanged(string value) => OnPropertyChanged(nameof(Tooltip));
}
