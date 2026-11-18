using NotchPC.Core.Notifications;
using NotchPC.Core.Settings;
using NotchPC.Core.Widgets;

namespace NotchPC.Tests;

public sealed class NotificationTargetTests
{
    [Fact]
    public void Tab_target_round_trips()
    {
        Assert.Equal(TabIds.Widgets, NotificationTargets.TabOf(NotificationTargets.Tab(TabIds.Widgets)));
        Assert.Null(NotificationTargets.TabOf("https://example.com"));
        Assert.Null(NotificationTargets.TabOf(null));
    }

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("notch:widgets", false)]
    [InlineData(@"C:\x.exe", false)]
    [InlineData(null, false)]
    public void Only_web_links_are_opened(string? target, bool opens) =>
        Assert.Equal(opens, NotificationTargets.WebLinkOf(target) is not null);

    [Fact]
    public void Google_feed_opens_that_day()
    {
        var feed = new Uri("https://calendar.google.com/calendar/ical/x%40example.com/private-abc/basic.ics");
        Assert.Equal("https://calendar.google.com/calendar/r/day/2026/11/9", CalendarWebLink.For(feed, new DateTime(2026, 11, 9, 14, 0, 0))!.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://outlook.live.com/owa/calendar/abc/def/calendar.ics", "https://outlook.live.com/calendar/")]
    [InlineData("https://outlook.office365.com/owa/calendar/abc/reachcalendar.ics", "https://outlook.office365.com/calendar/")]
    [InlineData("https://p12-caldav.icloud.com/published/2/abc", "https://www.icloud.com/calendar/")]
    public void Other_known_providers_open_their_calendar(string feed, string expected) =>
        Assert.Equal(expected, CalendarWebLink.For(new Uri(feed), DateTime.Today)!.AbsoluteUri);

    [Fact]
    public void Unknown_provider_has_no_link()
    {
        Assert.Null(CalendarWebLink.For(new Uri("https://cal.example.org/feed.ics"), DateTime.Today));
        Assert.Null(CalendarWebLink.For(null, DateTime.Today));
    }
}
