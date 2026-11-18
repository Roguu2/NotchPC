using NotchPC.Core.Notifications;
using NotchPC.Core.Settings;
using NotchPC.Core.Widgets;

namespace NotchPC.Tests;

public sealed class NotificationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static NotchNotification N(string key, int minute, NotificationSource source = NotificationSource.App, string title = "t") =>
        new(key, source, "App", title, "", T0.AddMinutes(minute));

    [Fact]
    public void History_is_newest_first_and_replaces_same_key()
    {
        var h = new NotificationHistory();
        h.Add(N("a", 1));
        h.Add(N("b", 3));
        h.Add(N("c", 2));
        Assert.Equal(["b", "c", "a"], h.Items.Select(i => i.Key));

        Assert.True(h.Add(N("a", 5, title: "new")));
        Assert.Equal(["a", "b", "c"], h.Items.Select(i => i.Key));
        Assert.Equal("new", h.Items[0].Title);
        Assert.False(h.Add(h.Items[0])); // identical: nothing changes
    }

    [Fact]
    public void History_keeps_the_limit()
    {
        var h = new NotificationHistory(limit: 3);
        for (var i = 0; i < 5; i++)
        {
            h.Add(N($"k{i}", i));
        }

        Assert.Equal(["k4", "k3", "k2"], h.Items.Select(i => i.Key));
    }

    [Fact]
    public void Reading_the_action_center_again_keeps_own_notifications()
    {
        var h = new NotificationHistory();
        h.Add(N("app:1", 1));
        h.Add(N("sys:1", 2, NotificationSource.System));
        h.ReplaceSystem([N("sys:2", 3, NotificationSource.System)]);

        Assert.Equal(["sys:2", "app:1"], h.Items.Select(i => i.Key));
    }

    [Theory]
    [InlineData(NotificationSource.App, true, false, true, UserPresence.AcceptsNotifications, true)]
    [InlineData(NotificationSource.App, false, true, true, UserPresence.AcceptsNotifications, false)]
    [InlineData(NotificationSource.System, true, false, true, UserPresence.AcceptsNotifications, false)]
    [InlineData(NotificationSource.System, true, true, true, UserPresence.Busy, false)]
    [InlineData(NotificationSource.System, true, true, false, UserPresence.Busy, true)]
    [InlineData(NotificationSource.App, true, true, true, UserPresence.Unknown, true)]
    public void Banner_policy(NotificationSource source, bool app, bool system, bool respectBusy, UserPresence presence, bool expected) =>
        Assert.Equal(expected, BannerPolicy.ShouldShow(N("x", 0, source), app, system, respectBusy, presence));

    [Fact]
    public void Toast_text_first_line_is_the_title()
    {
        Assert.Equal(("Anna", "Hi!\nSee you"), ToastText.Split(["Anna", " Hi! ", "", null, "See you"]));
        Assert.Equal(("Only", ""), ToastText.Split(["Only"]));
        Assert.Equal(("", ""), ToastText.Split([]));
    }

    [Fact]
    public void Reminder_is_planned_before_the_next_timed_event()
    {
        var now = new DateTime(2026, 11, 18, 9, 0, 0);
        CalendarEvent[] events =
        [
            new("All day", now.Date, now.Date.AddDays(1), AllDay: true),
            new("Past", now.AddHours(-1), now.AddMinutes(-30), AllDay: false),
            new("Meeting", now.AddHours(1), now.AddHours(2), AllDay: false),
            new("Later", now.AddHours(3), now.AddHours(4), AllDay: false),
        ];
        var reminded = new HashSet<string>();

        var next = ReminderPlanner.Next(events, now, 10, reminded);
        Assert.Equal("Meeting", next!.Value.Event.Title);
        Assert.Equal(now.AddMinutes(50), next.Value.FireAt);

        reminded.Add(ReminderPlanner.KeyOf(next.Value.Event));
        Assert.Equal("Later", ReminderPlanner.Next(events, now, 10, reminded)!.Value.Event.Title);
    }

    [Fact]
    public void Reminder_fires_now_when_the_lead_time_already_passed_and_off_when_zero()
    {
        var now = new DateTime(2026, 11, 18, 9, 55, 0);
        CalendarEvent[] events = [new("Soon", new DateTime(2026, 11, 18, 10, 0, 0), new DateTime(2026, 11, 18, 11, 0, 0), AllDay: false)];

        Assert.Equal(now, ReminderPlanner.Next(events, now, 10, new HashSet<string>())!.Value.FireAt);
        Assert.Null(ReminderPlanner.Next(events, now, 0, new HashSet<string>()));
    }

    [Fact]
    public void Notification_settings_are_clamped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NotchPC.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, """{ "notifications": { "bannerSeconds": 999, "calendarReminderMinutes": -5 } }""");
            var s = new SettingsStore(file).Load();
            Assert.Equal(30, s.Notifications.BannerSeconds);
            Assert.Equal(0, s.Notifications.CalendarReminderMinutes);
            Assert.True(s.Notifications.ShowSystemBanners);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
