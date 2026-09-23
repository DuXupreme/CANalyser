namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Filter parameters for PCAN/raw frame table.
/// </summary>
public sealed class RawFrameFilterOptions
{
    public string? IdFilter { get; set; }

    public string? DataContainsHex { get; set; }

    /// <summary>Optional zero-based payload byte filter. Value is compared exactly (0..255).</summary>
    public int? ByteIndex { get; set; }

    public byte? ByteValue { get; set; }

    public string? TypeContains { get; set; }

    public string? ChannelContains { get; set; }

    public double? TimeStart { get; set; }

    public double? TimeEnd { get; set; }

    public bool? IsExtended { get; set; }

    public int MaxRows { get; set; } = 50_000;

    public int Offset { get; set; }
}
