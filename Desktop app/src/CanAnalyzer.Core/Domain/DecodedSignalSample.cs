using System.Globalization;
using System.Numerics;

namespace CanAnalyzer.Core.Domain;

public enum DecodeQuality
{
    Valid,
    PartialDataset
}

/// <summary>Stable signal identity; display labels are never used as data keys.</summary>
public sealed record SignalIdentity(
    string Channel,
    CanFrameFormat FrameFormat,
    bool IsExtended,
    uint FrameId,
    string MessageName,
    string SignalName)
{
    public string DisplayLabel =>
        $"{MessageName}.{SignalName} [{(string.IsNullOrWhiteSpace(Channel) ? "?" : Channel)} | " +
        $"{(FrameFormat == CanFrameFormat.FlexibleDataRate ? "FD" : "CAN")} | {(IsExtended ? "Ext" : "Std")} | 0x{FrameId:X}]";
}

/// <summary>One strictly decoded signal value with an exact raw value and source-frame link.</summary>
public sealed record DecodedSignalSample(
    long TimestampNanoseconds,
    long FrameIndex,
    long SourceLineNumber,
    SignalIdentity Identity,
    double Value,
    BigInteger RawValue,
    string Unit = "",
    DecodeQuality Quality = DecodeQuality.Valid)
{
    public double TimeSeconds => TimestampNanoseconds / 1_000_000_000d;
    public uint FrameId => Identity.FrameId;
    public string Channel => Identity.Channel;
    public string MessageName => Identity.MessageName;
    public string SignalName => Identity.SignalName;
    public string Label => Identity.DisplayLabel;

    public string RawValueHex
    {
        get
        {
            var hex = RawValue.ToString("X", CultureInfo.InvariantCulture).TrimStart('0');
            return $"0x{(hex.Length == 0 ? "0" : hex)}";
        }
    }
}
