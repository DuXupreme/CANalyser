namespace CanAnalyzer.Core.Analysis;

/// <summary>Fixed reasons for rejecting scientifically invalid delay-analysis input.</summary>
public enum DelayInputFailureReason
{
    DataGap,
    DuplicateTimestamp,
    NonMonotonicTimestamp,
    NonFiniteTime,
    NonFiniteValue,
    InvalidTimeWindow
}

/// <summary>Preserves ArgumentException compatibility while exposing a privacy-safe reason.</summary>
public static class DelayInputFailure
{
    private const string ReasonKey = "CanAnalyzer.DelayInputFailureReason";

    public static ArgumentException Create(DelayInputFailureReason reason)
    {
        var message = reason switch
        {
            DelayInputFailureReason.DataGap => "De latencyselectie overlapt een meetgat. Kies bij Joystick gebruiksanalyse een aaneengesloten tijdvenster zonder meetgat.",
            DelayInputFailureReason.DuplicateTimestamp => "De latencyselectie bevat dubbele tijdstempels. Kies een tijdvenster zonder dubbele tijdstempels of controleer de bronlog.",
            DelayInputFailureReason.NonMonotonicTimestamp => "Tijdstempels in de latencyselectie lopen terug. Kies een tijdvenster met strikt oplopende tijdstempels of controleer de bronlog.",
            DelayInputFailureReason.NonFiniteTime => "De latencyselectie bevat een niet-eindig tijdstip (NaN of oneindig). Kies een geldige meetperiode of controleer de bronlog.",
            DelayInputFailureReason.NonFiniteValue => "De latencyselectie bevat een niet-eindige meetwaarde (NaN of oneindig). Kies een geldige meetperiode of controleer de signaaldecodering.",
            DelayInputFailureReason.InvalidTimeWindow => "Het gekozen tijdvenster is ongeldig. Vul bij Joystick gebruiksanalyse eindige begin- en eindtijden in, met de eindtijd na de begintijd.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        var exception = new ArgumentException(message);
        exception.Data[ReasonKey] = reason;
        return exception;
    }

    /// <summary>Never derives telemetry from an exception message, signal label, or arbitrary Data value.</summary>
    public static string GetTelemetryCode(ArgumentException exception) => exception.Data[ReasonKey] switch
    {
        DelayInputFailureReason.DataGap => "data_gap",
        DelayInputFailureReason.DuplicateTimestamp => "duplicate_timestamp",
        DelayInputFailureReason.NonMonotonicTimestamp => "non_monotonic_timestamp",
        DelayInputFailureReason.NonFiniteTime => "non_finite_time",
        DelayInputFailureReason.NonFiniteValue => "non_finite_value",
        DelayInputFailureReason.InvalidTimeWindow => "invalid_time_window",
        _ => "other_invalid_input"
    };
}
