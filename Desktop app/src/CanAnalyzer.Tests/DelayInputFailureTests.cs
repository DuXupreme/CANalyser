using CanAnalyzer.Core.Analysis;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class DelayInputFailureTests
{
    [Theory]
    [InlineData(DelayInputFailureReason.DataGap, "data_gap")]
    [InlineData(DelayInputFailureReason.DuplicateTimestamp, "duplicate_timestamp")]
    [InlineData(DelayInputFailureReason.NonMonotonicTimestamp, "non_monotonic_timestamp")]
    [InlineData(DelayInputFailureReason.NonFiniteTime, "non_finite_time")]
    [InlineData(DelayInputFailureReason.NonFiniteValue, "non_finite_value")]
    [InlineData(DelayInputFailureReason.InvalidTimeWindow, "invalid_time_window")]
    public void ReasonCodes_PreserveArgumentExceptionAndExplainNextStep(DelayInputFailureReason reason, string code)
    {
        var exception = DelayInputFailure.Create(reason);
        Assert.IsType<ArgumentException>(exception);
        Assert.Equal(code, DelayInputFailure.GetTelemetryCode(exception));
        Assert.True(exception.Message.Contains("Kies", StringComparison.Ordinal)
            || exception.Message.Contains("Vul", StringComparison.Ordinal));
    }

    [Fact]
    public void UntaggedOrUnrecognizedData_UsesFixedFallbackWithoutReadingMessages()
    {
        var exception = new ArgumentException("private signal name and measurement must never become a reason");
        Assert.Equal("other_invalid_input", DelayInputFailure.GetTelemetryCode(exception));
        exception.Data["CanAnalyzer.DelayInputFailureReason"] = "private arbitrary Data";
        Assert.Equal("other_invalid_input", DelayInputFailure.GetTelemetryCode(exception));
        exception.Data["CanAnalyzer.DelayInputFailureReason"] = (DelayInputFailureReason)999;
        Assert.Equal("other_invalid_input", DelayInputFailure.GetTelemetryCode(exception));
    }

    [Theory]
    [InlineData("duplicate_timestamp")]
    [InlineData("non_monotonic_timestamp")]
    [InlineData("non_finite_time")]
    [InlineData("non_finite_value")]
    public void BothDelayAlgorithms_RejectInvalidCommandAndResponseWithoutChangingInput(string reason)
    {
        var invalid = new SignalSeries("synthetic private label", [0, .1, .2, .3], [0, 0, 1, 0]);
        var valid = new SignalSeries("valid", [0, .1, .2, .3], [0, 0, 1, 0]);
        switch (reason)
        {
            case "duplicate_timestamp": invalid.Time[2] = .1; break;
            case "non_monotonic_timestamp": invalid.Time[2] = .05; break;
            case "non_finite_time": invalid.Time[2] = double.PositiveInfinity; break;
            case "non_finite_value": invalid.Value[2] = double.NegativeInfinity; break;
        }
        var originalTime = invalid.Time.ToArray();
        var originalValue = invalid.Value.ToArray();
        var service = new JoystickAnalyticsService();
        foreach (var (command, response) in new[] { (invalid, valid), (valid, invalid) })
        {
            var correlationFailure = Assert.Throws<ArgumentException>(() => service.AnalyzeDelay(command, response));
            var firstResponseFailure = Assert.Throws<ArgumentException>(() => service.AnalyzeFirstResponseDelay(command, response));
            Assert.Equal(reason, DelayInputFailure.GetTelemetryCode(correlationFailure));
            Assert.Equal(reason, DelayInputFailure.GetTelemetryCode(firstResponseFailure));
            Assert.DoesNotContain(invalid.Label, correlationFailure.Message);
            Assert.DoesNotContain(invalid.Label, firstResponseFailure.Message);
        }
        Assert.Equal(originalTime, invalid.Time);
        Assert.Equal(originalValue, invalid.Value);
    }
}
