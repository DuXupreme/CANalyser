using CanAnalyzer.Core.Analysis;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class ActuatorCsvImportTests
{
    private const string Header =
        "pc_timestamp,arduino_time_ms,mode,command_position_pct,target_position_pct," +
        "actual_position_pct,error_pct,pwm,current_a,filtered_current_a,peak_current_a," +
        "bus_voltage_v,shunt_voltage_mv,power_w,fault_code,fault_text,fault_latched," +
        "lower_limit,upper_limit,estop";

    [Fact]
    public async Task MultipleRunsAreAlignedOnFirstStepTargetTransition()
    {
        var root = Directory.CreateTempSubdirectory("canalyser_actuator_").FullName;
        var first = WriteLog(root, "actuator_run_a.csv", 1000, 25, 85);
        var second = WriteLog(root, "actuator_run_b.csv", 5000, 80, 20);
        try
        {
            var dataset = await new ActuatorCsvImportService().ImportAsync(
                [first, second], null, CancellationToken.None);

            Assert.Equal(32, dataset.SignalCount);
            var runA = Assert.Single(dataset.SignalSeriesByLabel.Values,
                series => series.Identity.Channel == "run_a" &&
                          series.Identity.SignalName == "ActualPositionPct");
            var runB = Assert.Single(dataset.SignalSeriesByLabel.Values,
                series => series.Identity.Channel == "run_b" &&
                          series.Identity.SignalName == "ActualPositionPct");
            Assert.Equal([-0.2, -0.1, 0.0, 0.1], runA.Time, new DoubleArrayComparer(1e-9));
            Assert.Equal([-0.2, -0.1, 0.0, 0.1], runB.Time, new DoubleArrayComparer(1e-9));
            Assert.Equal([25d, 25d, 25d, 40d], runA.Value);
            Assert.Equal([80d, 80d, 80d, 65d], runB.Value);
            Assert.Contains("2 run(s)", dataset.Diagnostics.DecodeNote);
            Assert.True(dataset.ImportReport?.IsConsistent);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MissingRequiredColumnIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"actuator_bad_{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "arduino_time_ms,mode\n1,STEP\n");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ActuatorCsvImportService().ImportAsync([path], null, CancellationToken.None));
            Assert.Contains("mist verplichte kolommen", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteLog(string root, string name, long startMs, double startTarget, double endTarget)
    {
        var path = Path.Combine(root, name);
        var startActual = startTarget;
        var endActual = startTarget < endTarget ? startTarget + 15 : startTarget - 15;
        var rows = new[]
        {
            Row(startMs, "STEP", startTarget, startActual),
            Row(startMs + 100, "STEP", startTarget, startActual),
            Row(startMs + 200, "STEP", endTarget, startActual),
            Row(startMs + 300, "STEP", endTarget, endActual)
        };
        File.WriteAllLines(path, [Header, .. rows]);
        return path;
    }

    private static string Row(long time, string mode, double target, double actual) =>
        $"2026-01-01T12:00:00.000,{time},{mode},50,{target},{actual},{target-actual},100," +
        "1.2,1.1,2.0,24.0,18.0,28.8,0,No fault,0,0,0,0";

    private sealed class DoubleArrayComparer(double tolerance) : IEqualityComparer<double[]>
    {
        public bool Equals(double[]? x, double[]? y) =>
            x is not null && y is not null && x.Length == y.Length &&
            x.Zip(y).All(pair => Math.Abs(pair.First - pair.Second) <= tolerance);

        public int GetHashCode(double[] obj) => obj.Length;
    }
}
