using System.Net;
using System.Text;
using NotchPC.Core.Widgets;

namespace NotchPC.Tests;

public sealed class FocusTimerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Idle_timer_shows_full_focus_length()
    {
        var t = new FocusTimer(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));

        Assert.Equal(FocusState.Idle, t.State);
        Assert.Equal(TimeSpan.FromMinutes(25), t.Remaining(T0));
        Assert.Equal("25:00", FocusTimer.Format(t.Remaining(T0)));
    }

    [Fact]
    public void Running_counts_down_and_pause_freezes()
    {
        var t = new FocusTimer(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
        t.Start(T0);

        Assert.Equal(TimeSpan.FromMinutes(15), t.Remaining(T0.AddMinutes(10)));
        t.Pause(T0.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(15), t.Remaining(T0.AddHours(3)));

        t.Start(T0.AddHours(3));
        Assert.Equal(T0.AddHours(3).AddMinutes(15), t.EndsAt);
    }

    [Fact]
    public void Focus_end_starts_the_break_and_the_break_end_stops()
    {
        var t = new FocusTimer(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
        t.Start(T0);

        Assert.Null(t.CompleteIfDue(T0.AddMinutes(24)));
        Assert.Equal(FocusPhase.Focus, t.CompleteIfDue(T0.AddMinutes(25)));
        Assert.Equal(FocusPhase.Break, t.Phase);
        Assert.Equal(FocusState.Running, t.State);
        Assert.Equal(T0.AddMinutes(30), t.EndsAt);
        Assert.Equal(FocusPhase.Break, t.CompleteIfDue(T0.AddMinutes(30)));
        Assert.Equal(FocusPhase.Focus, t.Phase);
        Assert.Equal(FocusState.Idle, t.State);
        Assert.Equal(TimeSpan.FromMinutes(25), t.Remaining(T0.AddMinutes(31)));
    }

    [Fact]
    public void After_sleeping_through_phases_the_new_phase_starts_now()
    {
        var t = new FocusTimer(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
        t.Start(T0);

        t.CompleteIfDue(T0.AddHours(2));

        Assert.Equal(T0.AddHours(2).AddMinutes(5), t.EndsAt);
    }

    [Fact]
    public void Plus_minus_change_length_within_limits()
    {
        var t = new FocusTimer(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));

        t.Adjust(FocusTimer.Step, T0);
        Assert.Equal(TimeSpan.FromMinutes(30), t.Remaining(T0));
        for (var i = 0; i < 10; i++)
        {
            t.Adjust(-FocusTimer.Step, T0);
        }

        Assert.Equal(FocusTimer.MinLength, t.FocusLength);
    }

    [Fact]
    public void Adjust_while_running_moves_the_end_but_keeps_at_least_a_minute()
    {
        var t = new FocusTimer(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5));
        t.Start(T0);

        t.Adjust(FocusTimer.Step, T0.AddMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(20), t.Remaining(T0.AddMinutes(10)));

        t.Adjust(-TimeSpan.FromMinutes(25), T0.AddMinutes(29));
        Assert.Equal(TimeSpan.FromMinutes(1), t.Remaining(T0.AddMinutes(29)));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59.2, "01:00")]
    [InlineData(299, "04:59")]
    [InlineData(3900, "1:05:00")]
    public void Formats_countdown_rounding_up(double seconds, string expected)
    {
        Assert.Equal(expected, FocusTimer.Format(TimeSpan.FromSeconds(seconds)));
    }
}

public sealed class WeatherTests
{
    [Theory]
    [InlineData(0, WeatherKind.Clear)]
    [InlineData(2, WeatherKind.PartlyCloudy)]
    [InlineData(45, WeatherKind.Fog)]
    [InlineData(61, WeatherKind.Rain)]
    [InlineData(67, WeatherKind.FreezingRain)]
    [InlineData(75, WeatherKind.Snow)]
    [InlineData(81, WeatherKind.Showers)]
    [InlineData(99, WeatherKind.Thunderstorm)]
    public void Maps_wmo_codes(int code, WeatherKind kind)
    {
        Assert.Equal(kind, WeatherCodes.KindOf(code));
    }

    [Fact]
    public async Task Parses_forecast_response()
    {
        const string json = """
            {"current":{"time":"2026-11-17T20:00","interval":900,"temperature_2m":5.6,"weather_code":61,"is_day":0},
             "daily":{"time":["2026-11-17"],"temperature_2m_max":[8.8],"temperature_2m_min":[4.7]}}
            """;
        var client = new OpenMeteoClient(new HttpClient(new Stub(json)));

        var w = await client.GetCurrentAsync(52.52, 13.41, CancellationToken.None);

        Assert.Equal(5.6, w.TemperatureC);
        Assert.Equal(WeatherKind.Rain, w.Kind);
        Assert.False(w.IsDay);
        Assert.Equal((8.8, 4.7), (w.MaxC, w.MinC));
    }

    [Fact]
    public async Task Parses_geocoding_results()
    {
        const string json = """{"results":[{"name":"Berlin","country":"Niemcy","admin1":"Land Berlin","latitude":52.52437,"longitude":13.41053}]}""";
        var client = new OpenMeteoClient(new HttpClient(new Stub(json)));

        var places = await client.SearchAsync("Berlin", "pl", CancellationToken.None);

        Assert.Equal("Berlin, Land Berlin, Niemcy", places.Single().Display);
    }

    [Fact]
    public async Task No_results_is_an_empty_list()
    {
        var client = new OpenMeteoClient(new HttpClient(new Stub("""{"generationtime_ms":0.1}""")));

        Assert.Empty(await client.SearchAsync("Qwxzzz", "pl", CancellationToken.None));
    }

    [Fact]
    public async Task Server_error_becomes_weather_unavailable()
    {
        var client = new OpenMeteoClient(new HttpClient(new Stub("oops", HttpStatusCode.BadGateway)));

        await Assert.ThrowsAsync<WeatherUnavailableException>(() => client.GetCurrentAsync(1, 2, CancellationToken.None));
    }

    private sealed class Stub(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

public sealed class IcsCalendarTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

    private const string Ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:one@test
        DTSTART:20261117T193000Z
        DTEND:20261117T203000Z
        SUMMARY:Design review
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly@test
        DTSTART;TZID=Europe/Warsaw:20261102T090000
        DTEND;TZID=Europe/Warsaw:20261102T093000
        RRULE:FREQ=WEEKLY;BYDAY=MO
        SUMMARY:Standup
        END:VEVENT
        BEGIN:VEVENT
        UID:allday@test
        DTSTART;VALUE=DATE:20261118
        DTEND;VALUE=DATE:20261119
        SUMMARY:Urodziny
        END:VEVENT
        BEGIN:VEVENT
        UID:cancelled@test
        DTSTART:20261117T180000Z
        DTEND:20261117T190000Z
        STATUS:CANCELLED
        SUMMARY:Odwołane
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Lists_single_recurring_and_all_day_events_in_local_time()
    {
        var events = IcsCalendar.Upcoming(Ics, new DateTime(2026, 11, 17, 12, 0, 0), TimeSpan.FromDays(7), Warsaw);

        Assert.Equal(["Design review", "Urodziny", "Standup"], events.Select(e => e.Title));
        Assert.Equal(new DateTime(2026, 11, 17, 20, 30, 0), events[0].StartLocal); // 19:30Z = 20:30 CET
        Assert.True(events[1].AllDay);
        Assert.Equal(new DateTime(2026, 11, 23, 9, 0, 0), events[2].StartLocal); // next Monday
    }

    [Fact]
    public void Cancelled_events_are_skipped()
    {
        var events = IcsCalendar.Upcoming(Ics, new DateTime(2026, 11, 17, 12, 0, 0), TimeSpan.FromDays(1), Warsaw);

        Assert.DoesNotContain(events, e => e.Title == "Odwołane");
    }

    [Fact]
    public void Running_event_is_still_next_until_it_ends()
    {
        var now = new DateTime(2026, 11, 17, 21, 0, 0);
        var events = IcsCalendar.Upcoming(Ics, now, TimeSpan.FromDays(7), Warsaw);

        Assert.Equal("Design review", IcsCalendar.Next(events, now)?.Title);
        Assert.Equal("Urodziny", IcsCalendar.Next(events, new DateTime(2026, 11, 17, 21, 31, 0))?.Title);
    }

    [Fact]
    public void Garbage_is_a_format_error()
    {
        Assert.Throws<FormatException>(() => IcsCalendar.Upcoming("<html>login page</html>", DateTime.Now, TimeSpan.FromDays(1), Warsaw));
    }

    [Theory]
    [InlineData("webcal://example.com/cal.ics", "https://example.com/cal.ics")]
    [InlineData(" https://calendar.google.com/calendar/ical/x/private-y/basic.ics ", "https://calendar.google.com/calendar/ical/x/private-y/basic.ics")]
    [InlineData("http://insecure.example/cal.ics", null)]
    [InlineData("not a url", null)]
    public void Normalizes_feed_urls(string input, string? expected)
    {
        Assert.Equal(expected, IcsCalendar.NormalizeUrl(input)?.ToString());
    }
}

public sealed class SensorProtocolTests
{
    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(950, "950 B/s")]
    [InlineData(176_400, "176 KB/s")]
    [InlineData(2_300_000, "2.3 MB/s")]
    [InlineData(1_250_000_000, "1.3 GB/s")]
    public void Formats_rates(double bps, string expected)
    {
        Assert.Equal(expected, SensorProtocol.FormatRate(bps));
    }

    [Fact]
    public void Snapshot_round_trips_and_missing_temps_stay_null()
    {
        var s = new SensorSnapshot { CpuLoad = 31, GpuLoad = 58, GpuTemp = 66, RamLoad = 47, NetDown = 2_300_000 };

        var back = SensorProtocol.Deserialize(SensorProtocol.Serialize(s));

        Assert.Equal(s, back);
        Assert.Null(back!.CpuTemp);
        Assert.Null(SensorProtocol.Deserialize("{broken"));
    }
}
