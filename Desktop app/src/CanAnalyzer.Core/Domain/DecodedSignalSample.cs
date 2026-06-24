namespace CanAnalyzer.Core.Domain;

/// <summary>
/// One decoded signal value at a timestamp.
/// </summary>
public sealed record DecodedSignalSample(
    float TimeSeconds,
    uint FrameId,
    string MessageName,
    string SignalName,
    float Value,
    string RawValueHex = "",
    string Unit = "")
{
    public string Label => $"{MessageName}.{SignalName} [0x{FrameId:X}]";
}
