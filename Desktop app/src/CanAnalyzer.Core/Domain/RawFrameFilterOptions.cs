namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Filter parameters for PCAN/raw frame table.
/// </summary>
public sealed class RawFrameFilterOptions
{
    public string? IdFilter { get; set; }

    public string? DataContainsHex { get; set; }

    public string? TypeContains { get; set; }

    public string? ChannelContains { get; set; }

    public double? TimeStart { get; set; }

    public double? TimeEnd { get; set; }

    public bool? IsExtended { get; set; }

    public int MaxRows { get; set; } = 50_000;
}
