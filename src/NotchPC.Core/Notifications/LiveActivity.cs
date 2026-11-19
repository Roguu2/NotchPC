using System.Globalization;

namespace NotchPC.Core.Notifications;

public enum LiveActivityClock
{
    /// <summary>Time left until <see cref="LiveActivity.At"/> (timer, meeting).</summary>
    Countdown,

    /// <summary>Time since <see cref="LiveActivity.At"/> (recording).</summary>
    Elapsed,
}

/// <summary>
/// Something going on right now, shown in the closed pill like the iPhone's Live Activities: a running focus timer,
/// a meeting that starts soon, a screen recording. The highest <see cref="Priority"/> wins the pill.
/// </summary>
public sealed record LiveActivity(string Id, string Glyph, string Label, LiveActivityClock Clock, DateTimeOffset At, int Priority, string? ColorHex = null)
{
    public const string Timer = "timer";
    public const string Meeting = "meeting";
    public const string Recording = "recording";
}

public static class LiveActivities
{
    /// <summary>"24:59", "1:05:00", or "0:00" when the countdown is over.</summary>
    public static string Clock(LiveActivity a, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(a);
        var span = a.Clock == LiveActivityClock.Countdown ? a.At - now : now - a.At;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        // A countdown rounds up (shows 0:01 until the last second is gone), like any kitchen timer.
        var seconds = a.Clock == LiveActivityClock.Countdown ? (long)Math.Ceiling(span.TotalSeconds) : (long)span.TotalSeconds;
        var h = seconds / 3600;
        var m = seconds / 60 % 60;
        var s = seconds % 60;
        var c = CultureInfo.InvariantCulture;
        return h > 0 ? string.Create(c, $"{h}:{m:00}:{s:00}") : string.Create(c, $"{m}:{s:00}");
    }

    /// <summary>The activity the pill shows: highest priority, then the newest.</summary>
    public static LiveActivity? Top(IEnumerable<LiveActivity> activities) =>
        activities.OrderByDescending(a => a.Priority).ThenByDescending(a => a.At).FirstOrDefault();

    /// <summary>A calendar event becomes an activity from <paramref name="lead"/> before it starts until it has run 5 minutes.</summary>
    public static bool MeetingIsSoon(DateTime startLocal, DateTime nowLocal, TimeSpan lead) =>
        startLocal - nowLocal <= lead && nowLocal - startLocal <= TimeSpan.FromMinutes(5);
}
