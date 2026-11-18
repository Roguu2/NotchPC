using System.Globalization;

namespace NotchPC.Core.Widgets;

/// <summary>
/// The web page of the calendar an ICS feed comes from, so a reminder can open it. An ICS feed has no link to a
/// single event, so this opens the day (Google) or the calendar (Outlook, iCloud). Unknown providers: null.
/// </summary>
public static class CalendarWebLink
{
    public static Uri? For(Uri? feed, DateTime day)
    {
        if (feed is null)
        {
            return null;
        }

        var host = feed.Host.ToLowerInvariant();
        if (host == "calendar.google.com")
        {
            return new Uri(string.Create(CultureInfo.InvariantCulture, $"https://calendar.google.com/calendar/r/day/{day.Year}/{day.Month}/{day.Day}"));
        }

        if (host is "outlook.live.com" or "outlook.office.com" or "outlook.office365.com")
        {
            return new Uri($"https://{host}/calendar/");
        }

        if (host.EndsWith(".icloud.com", StringComparison.Ordinal))
        {
            return new Uri("https://www.icloud.com/calendar/");
        }

        return null;
    }
}
