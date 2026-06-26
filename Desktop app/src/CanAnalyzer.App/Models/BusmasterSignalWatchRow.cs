using System.Globalization;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Models;

/// <summary>
/// Current/summary value for one decoded signal in the BUSMASTER Signal Watch view.
/// </summary>
public sealed class BusmasterSignalWatchRow
{
    public BusmasterSignalWatchRow(
        SignalIdentity identity,
        long lastTimestampNanoseconds,
        long lastFrameIndex,
        double lastValue,
        string rawValueHex,
        string unit,
        int updateCount,
        double minimum,
        double maximum)
    {
        Identity = identity;
        Name = identity.SignalName;
        MessageName = identity.MessageName;
        Channel = string.IsNullOrWhiteSpace(identity.Channel) ? "1" : identity.Channel;
        FrameIdHex = $"0x{identity.FrameId:X}";
        LastTimeSeconds = (lastTimestampNanoseconds / 1_000_000_000d).ToString("G17", CultureInfo.CurrentCulture);
        LastFrameIndex = lastFrameIndex;
        PhysicalValue = lastValue.ToString("G15", CultureInfo.CurrentCulture);
        RawValue = rawValueHex;
        Unit = unit;
        UpdateCount = updateCount;
        Minimum = minimum.ToString("G15", CultureInfo.CurrentCulture);
        Maximum = maximum.ToString("G15", CultureInfo.CurrentCulture);
    }

    public SignalIdentity Identity { get; }

    public string Name { get; }

    public string MessageName { get; }

    public string Channel { get; }

    public string FrameIdHex { get; }

    public string LastTimeSeconds { get; }

    public long LastFrameIndex { get; }

    public string PhysicalValue { get; }

    public string RawValue { get; }

    public string Unit { get; }

    public int UpdateCount { get; }

    public string Minimum { get; }

    public string Maximum { get; }
}
