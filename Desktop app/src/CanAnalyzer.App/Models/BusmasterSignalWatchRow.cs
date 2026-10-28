using System.Globalization;
using CanAnalyzer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CanAnalyzer.App.Models;

/// <summary>
/// Current/summary value for one decoded signal in the BUSMASTER Signal Watch view.
/// </summary>
public sealed partial class BusmasterSignalWatchRow : ObservableObject
{
    [ObservableProperty] private bool _isWatched;
    [ObservableProperty] private string _age = "—";
    [ObservableProperty] private string _status = "Laatste opname";
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

    public string LastTimeSeconds { get; private set; }

    public long LastFrameIndex { get; private set; }

    public string PhysicalValue { get; private set; }

    public string RawValue { get; private set; }

    public string Unit { get; private set; }

    public int UpdateCount { get; }

    public string Minimum { get; }

    public string Maximum { get; }

    public void UpdateSample(DecodedSignalSample? sample, long? cursor, string status)
    {
        LastTimeSeconds = sample is null ? "—" : sample.TimeSeconds.ToString("G17", CultureInfo.CurrentCulture);
        LastFrameIndex = sample?.FrameIndex ?? -1;
        PhysicalValue = sample?.Value.ToString("G17", CultureInfo.CurrentCulture) ?? "—";
        RawValue = sample?.RawValueHex ?? "—";
        if (sample is not null) Unit = sample.Unit;
        Age = sample is not null && cursor is { } time
            ? ((time - (double)sample.TimestampNanoseconds) / 1e9).ToString("G9", CultureInfo.CurrentCulture) + " s" : "—";
        Status = status;
        OnPropertyChanged(nameof(LastTimeSeconds)); OnPropertyChanged(nameof(LastFrameIndex));
        OnPropertyChanged(nameof(PhysicalValue)); OnPropertyChanged(nameof(RawValue)); OnPropertyChanged(nameof(Unit));
    }
}
