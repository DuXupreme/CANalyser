namespace CanAnalyzer.Core.Domain;

/// <summary>
/// Raw CAN frame as parsed from input logs.
/// </summary>
public sealed record RawCanFrame(
    double TimeSeconds,
    uint Id,
    byte Dlc,
    byte[] Data,
    string Type,
    string Channel,
    bool IsExtended)
{
    public string IdHex => $"0x{Id:X}";

    public string DataHex => BitConverter.ToString(Data).Replace("-", " ");

    public string DataAscii =>
        string.Concat(Data.Select(b => b is >= 32 and <= 126 ? (char)b : '.'));
}
