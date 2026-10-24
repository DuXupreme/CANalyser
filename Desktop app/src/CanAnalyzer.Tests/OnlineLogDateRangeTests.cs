using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.Core.Domain;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class OnlineLogDateRangeTests
{
    // Use the real Dutch DST rules, independently of the test runner's local time zone.
    private static readonly TimeZoneInfo Netherlands = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Amsterdam");

    [Theory]
    [InlineData(10, 25, 25, "2026-10-24T22:00:00.0000000+00:00", "2026-10-25T23:00:00.0000000+00:00", 25)]
    [InlineData(10, 24, 24, "2026-10-23T22:00:00.0000000+00:00", "2026-10-24T22:00:00.0000000+00:00", 24)]
    [InlineData(10, 26, 26, "2026-10-25T23:00:00.0000000+00:00", "2026-10-26T23:00:00.0000000+00:00", 24)]
    [InlineData(10, 24, 26, "2026-10-23T22:00:00.0000000+00:00", "2026-10-26T23:00:00.0000000+00:00", 73)]
    [InlineData(3, 29, 29, "2026-03-28T23:00:00.0000000+00:00", "2026-03-29T22:00:00.0000000+00:00", 23)]
    public async Task InclusiveDates_SendExactUtcBoundsToApi(
        int month, int firstDay, int lastDay, string expectedFrom, string expectedTo, int hours)
    {
        // Non-midnight inputs must still select whole calendar days.
        var (fromUtc, toUtc) = OnlineLogDateRange.FromInclusiveDates(
            new DateTime(2026, month, firstDay, 12, 34, 56),
            new DateTime(2026, month, lastDay, 18, 45, 0), Netherlands);
        Assert.Equal(Utc(expectedFrom), fromUtc);
        Assert.Equal(Utc(expectedTo), toUtc);
        Assert.Equal(TimeSpan.FromHours(hours), toUtc - fromUtc);
        Assert.Equal(TimeSpan.Zero, fromUtc.Offset);
        Assert.Equal(TimeSpan.Zero, toUtc.Offset);

        var calls = 0;
        using var service = Service(request =>
        {
            calls++;
            var query = Query(request);
            Assert.Equal(expectedFrom, query["from"]);
            Assert.Equal(expectedTo, query["to"]);
            return Payload(false, []);
        });
        await service.GetLogsAsync("machine-3", fromUtc, toUtc, default);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void LocalCalendarDates_PreserveExistingConversion(DateTimeKind kind)
    {
        foreach (var day in new[] { 24, 25, 26 })
        {
            var date = new DateTime(2026, 10, day, 12, 0, 0, kind);
            var (fromUtc, toUtc) = OnlineLogDateRange.FromInclusiveDates(date, date, TimeZoneInfo.Local);
            Assert.Equal(new DateTimeOffset(date.Date).ToUniversalTime(), fromUtc);
            Assert.Equal(new DateTimeOffset(date.Date.AddDays(1)).ToUniversalTime(), toUtc);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WinterTimeDay_RetrievesEveryMinuteAndBothRepeatedHoursExactlyOnce(bool paginate)
    {
        var start = Utc("2026-10-24T22:00:00Z");
        var end = Utc("2026-10-25T23:00:00Z");
        // One distinct recording per real minute, plus the final millisecond of the day.
        // The two neighbouring records must never leak into this half-open selection.
        var recordings = Enumerable.Range(0, 25 * 60)
            .Select(minute => new Recording($"minute-{minute:D4}", start.AddMinutes(minute)))
            .Append(new Recording("last-millisecond", end.AddMilliseconds(-1))).ToArray();
        var available = recordings
            .Append(new Recording("before-start", start.AddMilliseconds(-1)))
            .Append(new Recording("at-exclusive-end", end)).ToArray();
        var requests = new List<(DateTimeOffset From, DateTimeOffset To)>();
        var leaves = new List<(DateTimeOffset From, DateTimeOffset To)>();
        using var service = Service(request =>
        {
            var query = Query(request);
            var from = Utc(query["from"]);
            var to = Utc(query["to"]);
            Assert.Equal(TimeSpan.Zero, from.Offset);
            Assert.Equal(TimeSpan.Zero, to.Offset);
            Assert.True(start <= from && from < to && to <= end);
            requests.Add((from, to));
            // Fake the API's recording-time filter and newest-first page limit.
            var matching = available.Where(log => log.RecordedAt >= from && log.RecordedAt < to)
                .OrderByDescending(log => log.RecordedAt).ToArray();
            var limit = paginate ? 200 : int.MaxValue;
            var truncated = matching.Length > limit;
            if (!truncated) leaves.Add((from, to));
            return Payload(truncated, matching.Take(limit).Select(log => (object)new
            {
                key = log.Key, name = log.Key + ".MF4", machine = "Vlindermachine 3",
                logger = "48EDFD35", session = "00000008", sizeBytes = 100,
                // Identical wall-clock times retain their distinct +02:00/+01:00 offsets.
                recordedAt = TimeZoneInfo.ConvertTime(log.RecordedAt, Netherlands).ToString("O", CultureInfo.InvariantCulture),
                uploadedAt = "2026-10-27T12:00:00Z"
            }).ToArray());
        });

        var date = new DateTime(2026, 10, 25);
        var (fromUtc, toUtc) = OnlineLogDateRange.FromInclusiveDates(date, date, Netherlands);
        var result = await service.GetLogsAsync("machine-3", fromUtc, toUtc, default);

        Assert.Equal((start, end), requests[0]);
        Assert.Equal(paginate, requests.Count > 1);
        Assert.False(result.Truncated);
        Assert.Equal(Mdf4ImportLimits.MaximumFiles, result.MaximumSelection);
        Assert.Equal(recordings.Select(log => log.Key).Order(), result.Files.Select(log => log.Key).Order());
        Assert.Equal(recordings.OrderByDescending(log => log.RecordedAt).Select(log => log.RecordedAt),
            result.Files.Select(log => log.RecordedAt!.Value));

        var repeatedHour = result.Files.Where(log => log.RecordedAt!.Value.Hour == 2).ToArray();
        Assert.Equal(120, repeatedHour.Length);
        for (var minute = 0; minute < 60; minute++)
        {
            var pair = repeatedHour.Where(log => log.RecordedAt!.Value.Minute == minute)
                .OrderBy(log => log.RecordedAt).ToArray();
            Assert.Equal(2, pair.Length);
            Assert.NotEqual(pair[0].Key, pair[1].Key);
            Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, minute, 0, TimeSpan.FromHours(2)), pair[0].RecordedAt);
            Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, minute, 0, TimeSpan.FromHours(1)), pair[1].RecordedAt);
            Assert.Equal(TimeSpan.FromHours(1), pair[1].RecordedAt!.Value - pair[0].RecordedAt!.Value);
        }

        // Successful leaf requests must cover the entire 25-hour interval without gaps or overlap.
        var cursor = start;
        foreach (var leaf in leaves.OrderBy(leaf => leaf.From))
        {
            Assert.Equal(cursor, leaf.From);
            cursor = leaf.To;
        }
        Assert.Equal(end, cursor);
    }

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Query(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/logs", request.RequestUri!.AbsolutePath);
        var query = request.RequestUri.Query.TrimStart('?').Split('&')
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
        Assert.Equal("machine-3", query["machine"]);
        Assert.Equal("recording", query["timeBasis"]);
        return query;
    }

    private static string Payload(bool truncated, object[] files) => JsonSerializer.Serialize(new
    { files, truncated, maximumSelection = 200, timeBasis = "recording", unknownRecordingTimes = 0 });

    private static OnlineLogService Service(Func<HttpRequestMessage, string> respond) => new(
        new HttpClient(new Handler(respond)) { BaseAddress = new Uri("https://example.test/") });

    private sealed record Recording(string Key, DateTimeOffset RecordedAt);

    private sealed class Handler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
    }
}
