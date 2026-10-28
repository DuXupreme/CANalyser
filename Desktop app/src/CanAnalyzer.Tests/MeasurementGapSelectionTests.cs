using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class MeasurementGapSelectionTests
{
    [Fact]
    public void JointCoverageUsesOnlyParticipatingChannelsAndMergesOverlaps()
    {
        var report = new ImportReport("MDF4", 0, 0, 0, 0, [])
        {
            Gaps = [],
            GapsByChannel = new Dictionary<string, IReadOnlyList<MeasurementGap>>(StringComparer.OrdinalIgnoreCase)
            {
                ["CAN1"] = [new(4, 8), new(1, 3)],
                ["CAN2"] = [new(2, 5), new(12, 13)],
                ["CAN9"] = [new(9, 11)]
            }
        };
        Assert.Equal(new MeasurementGap[] { new(1, 8), new(12, 13) },
            MeasurementGapSelection.ForChannels(report, ["can1", "CAN2", "CAN1"]));
        Assert.Equal(report.GapsByChannel["CAN1"], MeasurementGapSelection.ForChannel(report, "can1"));
    }

    [Fact]
    public void ExplicitEmptyCoverageDoesNotFallBackToGlobalGaps()
    {
        var report = new ImportReport("MDF4", 0, 0, 0, 0, [])
        {
            Gaps = [new(1, 3)],
            GapsByChannel = new Dictionary<string, IReadOnlyList<MeasurementGap>> { ["9"] = [] }
        };
        Assert.Empty(MeasurementGapSelection.ForChannel(report, "9"));
        Assert.Equal(report.Gaps, MeasurementGapSelection.ForChannel(report, "legacy-channel"));
        Assert.Equal(report.Gaps, MeasurementGapSelection.ForChannel(report, null));
        Assert.Empty(MeasurementGapSelection.ForChannel(null, "1"));
    }
}
