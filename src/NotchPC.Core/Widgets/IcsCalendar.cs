using System.Globalization;
using Ical.Net;
using Ical.Net.DataTypes;
using Calendar = Ical.Net.Calendar;

namespace NotchPC.Core.Widgets;

public sealed record CalendarEvent(string Title, DateTime StartLocal, DateTime EndLocal, bool AllDay)
{
    /// <summary>True if the event takes place (even partly) on <paramref name="day"/>.</summary>
    public bool IsOn(DateOnly day)
    {
        var start = day.ToDateTime(TimeOnly.MinValue);
        return StartLocal < start.AddDays(1) && EndLocal > start;
    }
}

/// <summary>One day of the agenda list (only days that have events).</summary>
public sealed record AgendaDay(DateOnly Day, IReadOnlyList<CalendarEvent> Events);

/// <summary>A parsed iCalendar feed; parse once, then ask for any date range (month view, agenda).</summary>
public sealed class IcsFeed
{
    private readonly Calendar _calendar;

    private IcsFeed(Calendar calendar)
    {
        _calendar = calendar;
    }

    /// <exception cref="FormatException">The text is not a valid iCalendar file.</exception>
    public static IcsFeed Parse(string ics)
    {
        Calendar? calendar;
        try
        {
            calendar = Calendar.Load(ics);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new FormatException("Not a valid iCalendar file", ex);
        }

        return calendar is null ? throw new FormatException("Not a valid iCalendar file") : new IcsFeed(calendar);
    }

    /// <summary>
    /// Events overlapping [<paramref name="fromLocal"/>, <paramref name="toLocal"/>), sorted by start, in the given
    /// local time zone, including occurrences of recurring events. Cancelled events are skipped.
    /// </summary>
    public IReadOnlyList<CalendarEvent> Between(DateTime fromLocal, DateTime toLocal, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(fromLocal, DateTimeKind.Unspecified), zone);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(toLocal, DateTimeKind.Unspecified), zone);
        // Start a day earlier so events that began before "from" but are still running are included.
        var searchStart = new CalDateTime(fromUtc.AddDays(-1), CalDateTime.UtcTzId);

        var result = new List<CalendarEvent>();
        foreach (var ev in _calendar.Events)
        {
            if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var occurrence in ev.GetOccurrences(searchStart))
            {
                var period = occurrence.Period;
                var allDay = !period.StartTime.HasTime;
                var (startUtc, endUtc) = allDay
                    ? AllDayBounds(period, zone)
                    : (period.StartTime.AsUtc, (period.EffectiveEndTime ?? period.StartTime.Add(Duration.FromHours(1))).AsUtc);

                if (startUtc >= toUtc)
                {
                    break; // occurrences come in order
                }

                if (endUtc <= fromUtc)
                {
                    continue;
                }

                result.Add(new CalendarEvent(
                    string.IsNullOrWhiteSpace(ev.Summary) ? "—" : ev.Summary.Trim(),
                    TimeZoneInfo.ConvertTimeFromUtc(startUtc, zone),
                    TimeZoneInfo.ConvertTimeFromUtc(endUtc, zone),
                    allDay));
            }
        }

        return result.OrderBy(e => e.StartLocal).ThenBy(e => e.Title, StringComparer.CurrentCulture).ToList();
    }

    private static (DateTime StartUtc, DateTime EndUtc) AllDayBounds(Period period, TimeZoneInfo zone)
    {
        // All-day events are dates, not instants: they span local midnight to midnight.
        var startDate = period.StartTime.Date.ToDateTime(TimeOnly.MinValue);
        var endDate = period.EffectiveEndTime is { } end ? end.Date.ToDateTime(TimeOnly.MinValue) : startDate.AddDays(1);
        if (endDate <= startDate)
        {
            endDate = startDate.AddDays(1);
        }

        return (TimeZoneInfo.ConvertTimeToUtc(startDate, zone), TimeZoneInfo.ConvertTimeToUtc(endDate, zone));
    }
}

/// <summary>Helpers for the calendar widget (Google Calendar's secret iCal address and similar feeds).</summary>
public static class IcsCalendar
{
    /// <summary>Events overlapping [from, from + horizon) — see <see cref="IcsFeed.Between"/>.</summary>
    /// <exception cref="FormatException">The text is not a valid iCalendar file.</exception>
    public static IReadOnlyList<CalendarEvent> Upcoming(string ics, DateTime fromLocal, TimeSpan horizon, TimeZoneInfo zone) =>
        IcsFeed.Parse(ics).Between(fromLocal, fromLocal + horizon, zone);

    /// <summary>The next event that has not ended yet; timed events win over all-day ones on the same day.</summary>
    public static CalendarEvent? Next(IReadOnlyList<CalendarEvent> events, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events
            .Where(e => e.EndLocal > nowLocal)
            .OrderBy(e => e.AllDay ? e.StartLocal.Date.AddDays(1).AddTicks(-1) : e.StartLocal)
            .FirstOrDefault();
    }

    /// <summary>
    /// Days from <paramref name="fromDay"/> on (at most <paramref name="days"/> of them) that have events, each with
    /// its events (all-day first). A multi-day event appears on every day it covers; finished events are left out.
    /// </summary>
    public static IReadOnlyList<AgendaDay> Agenda(IEnumerable<CalendarEvent> events, DateOnly fromDay, int days, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events.Where(e => e.EndLocal > nowLocal).ToList();
        var result = new List<AgendaDay>();
        for (var i = 0; i < days; i++)
        {
            var day = fromDay.AddDays(i);
            var onDay = list.Where(e => e.IsOn(day)).OrderBy(e => !e.AllDay).ThenBy(e => e.StartLocal).ToList();
            if (onDay.Count > 0)
            {
                result.Add(new AgendaDay(day, onDay));
            }
        }

        return result;
    }

    /// <summary>The 42 days (6 weeks) shown for a month, starting on <paramref name="firstDayOfWeek"/>.</summary>
    public static IReadOnlyList<DateOnly> MonthGrid(int year, int month, DayOfWeek firstDayOfWeek)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = first.AddDays(-offset);
        return Enumerable.Range(0, 42).Select(start.AddDays).ToList();
    }

    /// <summary>webcal:// links (some calendar apps) are plain https.</summary>
    public static Uri? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed["webcal://".Length..];
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    /// <summary>Short day label for the agenda: "Dziś", "Jutro" are added by the UI; this is e.g. "śr 19.11".</summary>
    public static string ShortDay(DateOnly day, CultureInfo culture) =>
        day.ToString("ddd d.MM", culture);
}
