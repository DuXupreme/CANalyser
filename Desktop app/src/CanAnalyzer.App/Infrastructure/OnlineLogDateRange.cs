namespace CanAnalyzer.App.Infrastructure;

internal static class OnlineLogDateRange
{
    /// <summary>Converts inclusive calendar dates into a half-open UTC recording interval.</summary>
    internal static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) FromInclusiveDates(
        DateTime from, DateTime to, TimeZoneInfo timeZone)
    {
        // DatePicker values are calendar dates in the selected time zone, not UTC instants.
        var start = DateTime.SpecifyKind(from.Date, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(to.Date.AddDays(1), DateTimeKind.Unspecified);
        // Convert each local midnight separately: a calendar day can contain 23 or 25 hours.
        return (new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, timeZone)),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(end, timeZone)));
    }
}
