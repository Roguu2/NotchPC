using NotchPC.Core.Widgets;

namespace NotchPC.Core.Notifications;

public enum NotificationSource
{
    /// <summary>Made by NotchPC itself (timer, calendar reminder…).</summary>
    App,

    /// <summary>A Windows notification of another app, read from the Action Center.</summary>
    System,
}

/// <summary>One notification shown in the notch.</summary>
/// <param name="Key">Unique: "sys:{id}" for Windows notifications, anything unique for own ones.</param>
/// <param name="Glyph">Segoe Fluent Icons code point used when there is no app logo.</param>
public sealed record NotchNotification(
    string Key,
    NotificationSource Source,
    string AppName,
    string Title,
    string Body,
    DateTimeOffset Time,
    string? Glyph = null,
    uint? SystemId = null,
    string? AppId = null,
    string? Target = null);

/// <summary>
/// Where clicking a NotchPC notification leads. Windows notifications of other apps have no such target: Windows
/// does not give their launch arguments to other apps, so a click can only bring that app forward.
/// </summary>
public static class NotificationTargets
{
    private const string TabPrefix = "notch:";

    /// <summary>A tab of the notch (see TabIds).</summary>
    public static string Tab(string tabId) => TabPrefix + tabId;

    /// <summary>The tab id, or null when the target is not a notch tab.</summary>
    public static string? TabOf(string? target) =>
        target is not null && target.StartsWith(TabPrefix, StringComparison.Ordinal) ? target[TabPrefix.Length..] : null;

    /// <summary>Only plain web links are opened (never a file or another scheme taken from data).</summary>
    public static Uri? WebLinkOf(string? target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;
}

/// <summary>Newest first, one entry per key, limited. Pure logic for the notifications tab.</summary>
public sealed class NotificationHistory(int limit = 50)
{
    private readonly List<NotchNotification> _items = [];

    public IReadOnlyList<NotchNotification> Items => _items;

    /// <summary>Adds or replaces (same key) at the top. Returns false if nothing changed.</summary>
    public bool Add(NotchNotification n)
    {
        ArgumentNullException.ThrowIfNull(n);
        var i = _items.FindIndex(x => x.Key == n.Key);
        if (i >= 0)
        {
            if (_items[i] == n)
            {
                return false;
            }

            _items.RemoveAt(i);
        }

        _items.Insert(0, n);
        _items.Sort((a, b) => b.Time.CompareTo(a.Time));
        if (_items.Count > limit)
        {
            _items.RemoveRange(limit, _items.Count - limit);
        }

        return true;
    }

    public bool Remove(string key) => _items.RemoveAll(x => x.Key == key) > 0;

    /// <summary>Windows notifications are re-read from the Action Center: keep own ones, replace the system ones.</summary>
    public void ReplaceSystem(IEnumerable<NotchNotification> system)
    {
        _items.RemoveAll(x => x.Source == NotificationSource.System);
        foreach (var n in system)
        {
            _items.Add(n);
        }

        _items.Sort((a, b) => b.Time.CompareTo(a.Time));
        if (_items.Count > limit)
        {
            _items.RemoveRange(limit, _items.Count - limit);
        }
    }

    public void Clear() => _items.Clear();
}

/// <summary>What Windows says about the user right now (SHQueryUserNotificationState).</summary>
public enum UserPresence
{
    AcceptsNotifications,
    Busy,               // fullscreen app, presentation, Focus / Do not disturb
    Unknown,
}

public static class BannerPolicy
{
    /// <summary>Whether a new notification should pop up in the notch (it always lands in the history).</summary>
    public static bool ShouldShow(NotchNotification n, bool showAppBanners, bool showSystemBanners, bool respectBusy, UserPresence presence)
    {
        ArgumentNullException.ThrowIfNull(n);
        var allowed = n.Source == NotificationSource.App ? showAppBanners : showSystemBanners;
        return allowed && !(respectBusy && presence == UserPresence.Busy);
    }
}

public static class ToastText
{
    /// <summary>First text line = title, the rest = body (as Windows draws a toast).</summary>
    public static (string Title, string Body) Split(IEnumerable<string?> lines)
    {
        var clean = lines.Select(l => l?.Trim()).Where(l => !string.IsNullOrEmpty(l)).Select(l => l!).ToList();
        return clean.Count switch
        {
            0 => ("", ""),
            1 => (clean[0], ""),
            _ => (clean[0], string.Join("\n", clean.Skip(1))),
        };
    }
}

public static class ReminderPlanner
{
    /// <summary>
    /// The next timed event that still needs a reminder <paramref name="minutesBefore"/> minutes ahead, and when to
    /// fire it (local time; "now" if the reminder moment already passed but the event has not started).
    /// All-day events are skipped.
    /// </summary>
    public static (CalendarEvent Event, DateTime FireAt)? Next(
        IEnumerable<CalendarEvent> events, DateTime nowLocal, int minutesBefore, ISet<string> alreadyReminded)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(alreadyReminded);
        if (minutesBefore <= 0)
        {
            return null;
        }

        var lead = TimeSpan.FromMinutes(minutesBefore);
        foreach (var e in events.Where(e => !e.AllDay && e.StartLocal > nowLocal).OrderBy(e => e.StartLocal))
        {
            if (alreadyReminded.Contains(KeyOf(e)))
            {
                continue;
            }

            var fireAt = e.StartLocal - lead;
            return (e, fireAt < nowLocal ? nowLocal : fireAt);
        }

        return null;
    }

    public static string KeyOf(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.StartLocal.ToString("yyyy-MM-dd'T'HH'-'mm", System.Globalization.CultureInfo.InvariantCulture) + "|" + e.Title;
    }
}
