namespace NotchPC.Core.Music;

/// <summary>
/// What the system media session reports at one moment (SMTC). Immutable; a new snapshot arrives on every change.
/// </summary>
public sealed record MediaSnapshot
{
    /// <summary>App that owns the session (AppUserModelId or exe name, e.g. "Spotify.exe").</summary>
    public string SourceApp { get; init; } = "";

    public string Title { get; init; } = "";

    public string Artist { get; init; } = "";

    public string Album { get; init; } = "";

    public bool IsPlaying { get; init; }

    public double PlaybackRate { get; init; } = 1.0;

    /// <summary>Track length; zero when the app does not report a timeline.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Position at <see cref="PositionUpdatedAt"/>, relative to the start of the track.</summary>
    public TimeSpan Position { get; init; }

    /// <summary>Timeline start as reported by the app (almost always zero); seek requests are absolute.</summary>
    public TimeSpan TimelineStart { get; init; }

    public DateTimeOffset PositionUpdatedAt { get; init; }

    public bool CanPlayPause { get; init; }

    public bool CanGoNext { get; init; }

    public bool CanGoPrevious { get; init; }

    public bool CanSeek { get; init; }

    /// <summary>Identifies the track for change detection (thumbnail reload, lyrics lookup).</summary>
    public string TrackKey => $"{SourceApp}\u001F{Artist}\u001F{Title}\u001F{Album}";

    public bool HasTimeline => Duration > TimeSpan.Zero;
}

/// <summary>Turns the sparse SMTC position updates into a smooth clock.</summary>
public static class MediaClock
{
    /// <summary>
    /// Current position: last reported position plus the time elapsed since (if playing), clamped to the track.
    /// Apps update the timeline rarely (often only on play/pause/seek), so this is extrapolated locally.
    /// </summary>
    public static TimeSpan PositionAt(MediaSnapshot media, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(media);
        var position = media.Position;
        if (media.IsPlaying && media.PositionUpdatedAt != default)
        {
            var elapsed = now - media.PositionUpdatedAt;
            if (elapsed > TimeSpan.Zero)
            {
                var rate = media.PlaybackRate > 0 ? media.PlaybackRate : 1.0;
                position += TimeSpan.FromTicks((long)(elapsed.Ticks * rate));
            }
        }

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return media.HasTimeline && position > media.Duration ? media.Duration : position;
    }

    /// <summary>"1:09", "12:03", "1:02:03".</summary>
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}
