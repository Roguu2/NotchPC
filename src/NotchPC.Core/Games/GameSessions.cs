using System.Globalization;

namespace NotchPC.Core.Games;

/// <summary>One finished play session: which game, when, how long it was in front, how smooth it ran.</summary>
public sealed record GameSession(
    string Game,
    DateTimeOffset Start,
    double Minutes,
    double? AvgFps = null,
    double? LowFps = null,
    double? MaxFps = null,
    string? Exe = null);

/// <summary>Per-second FPS readings of one session; summarised when it ends.</summary>
public sealed class FpsSamples
{
    private readonly List<double> _values = [];

    public int Count => _values.Count;

    /// <summary>Zero readings (loading screens, alt-tab) are skipped: they would drag the average down.</summary>
    public void Add(double fps)
    {
        if (fps >= 1 && !double.IsNaN(fps) && !double.IsInfinity(fps))
        {
            _values.Add(fps);
        }
    }

    public double? Average => _values.Count == 0 ? null : _values.Average();

    public double? Max => _values.Count == 0 ? null : _values.Max();

    /// <summary>"Low": the 5th percentile of the per-second rates (stutters show here, single hitches do not).</summary>
    public double? Low
    {
        get
        {
            if (_values.Count == 0)
            {
                return null;
            }

            var sorted = _values.Order().ToList();
            return sorted[(int)Math.Floor((sorted.Count - 1) * 0.05)];
        }
    }
}

/// <summary>History of play sessions (pure helpers; the app stores the list).</summary>
public static class GameSessions
{
    /// <summary>Sessions kept (oldest dropped).</summary>
    public const int Keep = 300;

    /// <summary>Shorter sessions (a launcher in fullscreen, a quick check) are not recorded.</summary>
    public const double MinMinutes = 2;

    /// <summary>A game in the background ends its session after this long.</summary>
    public static readonly TimeSpan AwayLimit = TimeSpan.FromMinutes(10);

    public static List<GameSession> Add(IEnumerable<GameSession> history, GameSession session)
    {
        var list = history.Append(session).OrderBy(s => s.Start).ToList();
        return list.Count > Keep ? list.Skip(list.Count - Keep).ToList() : list;
    }

    /// <summary>Monday 00:00 (local time) of the week of <paramref name="now"/>.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset now)
    {
        var day = now.Date;
        var back = ((int)day.DayOfWeek + 6) % 7;
        var monday = day.AddDays(-back);
        return new DateTimeOffset(monday, now.Offset);
    }

    /// <summary>Minutes played since a moment, for one game (by name, any case) or all of them.</summary>
    public static double MinutesSince(IEnumerable<GameSession> history, DateTimeOffset since, string? game = null) =>
        history
            .Where(s => s.Start >= since && (game is null || string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase)))
            .Sum(s => s.Minutes);

    /// <summary>The last time a game was played (from the history), null if never.</summary>
    public static DateTimeOffset? LastPlayed(IEnumerable<GameSession> history, string game) =>
        history.Where(s => string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase))
            .Select(s => (DateTimeOffset?)s.Start.AddMinutes(s.Minutes))
            .DefaultIfEmpty(null)
            .Max();

    /// <summary>"2 h 14 min", "45 min", "1 h".</summary>
    public static string Duration(double minutes)
    {
        var m = (int)Math.Round(Math.Max(0, minutes));
        return m < 60 ? $"{m} min" : m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min";
    }

    /// <summary>"143" (rounded), or null without readings.</summary>
    public static string? Fps(double? fps, CultureInfo culture) => fps is { } f ? Math.Round(f).ToString("0", culture) : null;
}
