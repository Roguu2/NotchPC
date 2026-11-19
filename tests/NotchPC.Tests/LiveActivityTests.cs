using NotchPC.Core.Notifications;

namespace NotchPC.Tests;

public sealed class LiveActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1499.2, "25:00")]
    [InlineData(59.0, "0:59")]
    [InlineData(0.4, "0:01")]
    [InlineData(-3, "0:00")]
    [InlineData(3900, "1:05:00")]
    public void Countdown(double secondsLeft, string text) =>
        Assert.Equal(text, LiveActivities.Clock(new LiveActivity("t", "", "", LiveActivityClock.Countdown, T0.AddSeconds(secondsLeft), 1), T0));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(61.9, "1:01")]
    [InlineData(3725, "1:02:05")]
    public void Elapsed(double secondsAgo, string text) =>
        Assert.Equal(text, LiveActivities.Clock(new LiveActivity("r", "", "", LiveActivityClock.Elapsed, T0.AddSeconds(-secondsAgo), 1), T0));

    [Fact]
    public void Highest_priority_wins_the_pill()
    {
        var timer = new LiveActivity(LiveActivity.Timer, "", "", LiveActivityClock.Countdown, T0, 2);
        var rec = new LiveActivity(LiveActivity.Recording, "", "", LiveActivityClock.Elapsed, T0, 3);
        Assert.Equal(rec, LiveActivities.Top([timer, rec]));
        Assert.Null(LiveActivities.Top([]));
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(15, true)]
    [InlineData(1, true)]
    [InlineData(-4, true)]
    [InlineData(-6, false)]
    public void Meeting_window(int minutesUntilStart, bool soon)
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0);
        Assert.Equal(soon, LiveActivities.MeetingIsSoon(now.AddMinutes(minutesUntilStart), now, TimeSpan.FromMinutes(15)));
    }
}
