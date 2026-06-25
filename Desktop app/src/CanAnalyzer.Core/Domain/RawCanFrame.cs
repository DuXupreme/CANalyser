namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Frame transport format. The payload length is stored separately from the DLC code.
/// </summary>
public enum CanFrameFormat
{
    Classic,
    FlexibleDataRate
}

public enum CanFrameDirection
{
    Unknown,
    Receive,
    Transmit
}

public enum CanFrameKind
{
    Data,
    Remote,
    Error
}

/// <summary>
/// Raw CAN frame with exact relative time and complete source provenance.
/// </summary>
public sealed record RawCanFrame(
    long TimestampNanoseconds,
    uint Id,
    byte Dlc,
    byte[] Data,
    string Type,
    string Channel,
    bool IsExtended,
    long FrameIndex = -1,
    long SourceLineNumber = -1,
    CanFrameFormat FrameFormat = CanFrameFormat.Classic,
    CanFrameDirection Direction = CanFrameDirection.Unknown,
    CanFrameKind Kind = CanFrameKind.Data,
    bool BitRateSwitch = false,
    bool ErrorStateIndicator = false)
{
    /// <summary>Compatibility/view projection only; the stored timestamp is integer nanoseconds.</summary>
    public double TimeSeconds => TimestampNanoseconds / 1_000_000_000d;

    public int PayloadLength => Data.Length;

    public string IdHex => $"0x{Id:X}";

    public string DataHex => BitConverter.ToString(Data).Replace("-", " ");

    public string DataAscii =>
        string.Concat(Data.Select(b => b is >= 32 and <= 126 ? (char)b : '.'));

    /// <summary>Compatibility constructor for programmatic callers; parsers must use exact nanoseconds.</summary>
    public RawCanFrame(
        double timeSeconds,
        uint id,
        byte dlc,
        byte[] data,
        string type,
        string channel,
        bool isExtended)
        : this(
            checked((long)Math.Round(timeSeconds * 1_000_000_000d, MidpointRounding.AwayFromZero)),
            id,
            dlc,
            data,
            type,
            channel,
            isExtended)
    {
    }
}
