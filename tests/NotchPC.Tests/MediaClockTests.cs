using NotchPC.Core.Music;

namespace NotchPC.Tests;

public sealed class MediaClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static MediaSnapshot Track(bool playing, double positionSec, double rate = 1) => new()
    {
        Title = "x",
        IsPlaying = playing,
        PlaybackRate = rate,
        Duration = TimeSpan.FromSeconds(200),
        Position = TimeSpan.FromSeconds(positionSec),
        PositionUpdatedAt = T0,
    };

    [Fact]
    public void Playing_track_advances_from_last_report()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), MediaClock.PositionAt(Track(true, 10), T0.AddSeconds(5)));
    }

    [Fact]
    public void Paused_track_stays_put()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), MediaClock.PositionAt(Track(false, 10), T0.AddSeconds(5)));
    }

    [Fact]
    public void Playback_rate_is_applied()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), MediaClock.PositionAt(Track(true, 10, 2), T0.AddSeconds(5)));
    }

    [Fact]
    public void Position_is_clamped_to_duration()
    {
        Assert.Equal(TimeSpan.FromSeconds(200), MediaClock.PositionAt(Track(true, 195), T0.AddMinutes(5)));
    }

    [Theory]
    [InlineData(69, "1:09")]
    [InlineData(723, "12:03")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "0:00")]
    public void Formats_times(int seconds, string expected)
    {
        Assert.Equal(expected, MediaClock.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Cover_accent_prefers_saturated_pixels()
    {
        // Three grey pixels and one strong orange one (BGRA).
        byte[] px = [128, 128, 128, 255, 128, 128, 128, 255, 128, 128, 128, 255, 0, 120, 255, 255];

        var (r, g, b) = CoverColor.Accent(px)!.Value;

        Assert.True(r > g && g > b, $"expected orange-ish, got {r},{g},{b}");
        Assert.Null(CoverColor.Accent([0, 0, 0, 0]));
    }
}
