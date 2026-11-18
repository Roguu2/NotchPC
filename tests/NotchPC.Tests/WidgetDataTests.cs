using System.Text.Json;
using NotchPC.Core.Widgets;

namespace NotchPC.Tests;

public sealed class ForecastParsingTests
{
    [Fact]
    public void Hours_start_at_the_current_hour()
    {
        var hourly = JsonSerializer.Deserialize<OpenMeteoClient.HourlyBlock>("""
            {"time":["2026-11-17T21:00","2026-11-17T22:00","2026-11-17T23:00","2026-11-18T00:00"],
             "temperature_2m":[6.1,5.8,5.5,5.0],"weather_code":[3,61,61,0],"is_day":[0,0,0,0],
             "precipitation_probability":[10,80,null,5]}
            """)!;

        var hours = OpenMeteoClient.ParseHours(hourly, "2026-11-17T22:45");

        Assert.Equal(3, hours.Count);
        Assert.Equal(new DateTime(2026, 11, 17, 22, 0, 0), hours[0].Time);
        Assert.Equal(WeatherKind.Rain, hours[0].Kind);
        Assert.Equal(80, hours[0].PrecipitationChance);
        Assert.Equal(0, hours[1].PrecipitationChance); // null in the feed
    }

    [Fact]
    public void Days_start_tomorrow()
    {
        var daily = JsonSerializer.Deserialize<OpenMeteoClient.DailyBlock>("""
            {"time":["2026-11-17","2026-11-18","2026-11-19"],"weather_code":[3,71,0],
             "temperature_2m_max":[8.8,2.1,4.0],"temperature_2m_min":[4.7,-1.5,0.2],"precipitation_probability_max":[100,90,null]}
            """)!;

        var days = OpenMeteoClient.ParseDays(daily);

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 11, 18), days[0].Date);
        Assert.Equal(WeatherKind.Snow, days[0].Kind);
        Assert.Equal((2.1, -1.5), (days[0].MaxC, days[0].MinC));
    }
}

public sealed class CalendarViewTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0);

    [Fact]
    public void Month_grid_starts_on_the_first_day_of_the_week()
    {
        var grid = IcsCalendar.MonthGrid(2026, 11, DayOfWeek.Monday);

        Assert.Equal(42, grid.Count);
        Assert.Equal(new DateOnly(2026, 10, 26), grid[0]); // 1 Nov 2026 is a Sunday
        Assert.Equal(DayOfWeek.Monday, grid[0].DayOfWeek);
        Assert.Contains(new DateOnly(2026, 11, 30), grid);
    }

    [Fact]
    public void Agenda_groups_by_day_and_repeats_multi_day_events()
    {
        CalendarEvent[] events =
        [
            new("Past", Now.AddHours(-3), Now.AddHours(-2), false),
            new("Meeting", Now.AddHours(2), Now.AddHours(3), false),
            new("Trip", new DateTime(2026, 11, 18), new DateTime(2026, 11, 20), true),
            new("Dinner", new DateTime(2026, 11, 18, 19, 0, 0), new DateTime(2026, 11, 18, 21, 0, 0), false),
        ];

        var agenda = IcsCalendar.Agenda(events, DateOnly.FromDateTime(Now), 7, Now);

        Assert.Equal([new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 19)], agenda.Select(d => d.Day));
        Assert.Equal(["Meeting"], agenda[0].Events.Select(e => e.Title));
        Assert.Equal(["Trip", "Dinner"], agenda[1].Events.Select(e => e.Title)); // all-day first
        Assert.Equal(["Trip"], agenda[2].Events.Select(e => e.Title));
    }
}
