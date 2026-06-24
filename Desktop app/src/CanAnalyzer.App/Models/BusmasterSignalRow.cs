using System.Globalization;
using CanAnalyzer.Core.Domain;

namespace CanAnalyzer.App.Models;

/// <summary>
/// DBC interpretation of one signal in the selected Busmaster message.
/// </summary>
public sealed class BusmasterSignalRow
{
    public BusmasterSignalRow(DecodedSignalSample sample)
    {
        Name = sample.SignalName;
        PhysicalValue = sample.Value.ToString("0.######", CultureInfo.CurrentCulture);
        RawValue = sample.RawValueHex;
        Unit = sample.Unit;
    }

    public string Name { get; }

    public string PhysicalValue { get; }

    public string RawValue { get; }

    public string Unit { get; }
}
