using NotchPC.Core.Layout;

namespace NotchPC.Tests;

public sealed class NotchLayoutTests
{
    // Mirrors the developer's setup: 2560x1440 primary, 1920x1080 to the left.
    private static readonly MonitorDescriptor Primary = new(
        @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1440), new ScreenRect(0, 0, 2560, 1392), 1.0, IsPrimary: true);

    private static readonly MonitorDescriptor Left = new(
        @"\\.\DISPLAY2", new ScreenRect(-1920, 0, 0, 1080), new ScreenRect(-1920, 0, 0, 1032), 1.0, IsPrimary: false);

    [Fact]
    public void Empty_preference_selects_primary()
    {
        Assert.Same(Primary, NotchLayout.SelectMonitor([Left, Primary], ""));
    }

    [Fact]
    public void Preferred_monitor_is_matched_case_insensitively()
    {
        Assert.Same(Left, NotchLayout.SelectMonitor([Primary, Left], @"\\.\display2"));
    }

    [Fact]
    public void Unplugged_preferred_monitor_falls_back_to_primary()
    {
        Assert.Same(Primary, NotchLayout.SelectMonitor([Left, Primary], @"\\.\DISPLAY9"));
    }

    [Fact]
    public void No_primary_reported_falls_back_to_first()
    {
        var a = Left with { IsPrimary = false };
        Assert.Same(a, NotchLayout.SelectMonitor([a], null));
        Assert.Null(NotchLayout.SelectMonitor([], null));
    }

    [Fact]
    public void Window_is_centred_at_the_top_at_100_percent()
    {
        var r = NotchLayout.TopCenter(Primary, 560, 278);

        Assert.Equal(new ScreenRect(1000, 0, 1560, 278), r);
    }

    [Fact]
    public void Window_is_centred_on_a_monitor_left_of_primary()
    {
        var r = NotchLayout.TopCenter(Left, 560, 278);

        Assert.Equal(-1920 + ((1920 - 560) / 2), r.Left);
        Assert.Equal(560, r.Width);
    }

    [Fact]
    public void Dip_size_is_scaled_to_physical_pixels()
    {
        var scaled = Primary with { Scale = 1.5 };

        var r = NotchLayout.TopCenter(scaled, 560, 278);

        Assert.Equal(840, r.Width);
        Assert.Equal(417, r.Height);
        Assert.Equal((2560 - 840) / 2, r.Left);
    }

    [Fact]
    public void Card_offset_round_trips_on_a_monitor_left_of_primary()
    {
        var rect = NotchLayout.PlaceCard(Left, 300, 200, 380, 330);
        var (x, y) = NotchLayout.OffsetOf(rect, Left);

        Assert.Equal(-1920 + 300, rect.Left);
        Assert.Equal((300.0, 200.0), (x, y));
    }

    [Fact]
    public void Card_saved_off_screen_is_pulled_back_into_the_work_area()
    {
        // Saved at 2400,1300 (e.g. on a bigger screen); the taskbar takes the bottom 48 px.
        var rect = NotchLayout.PlaceCard(Primary, 2400, 1300, 380, 330);

        Assert.Equal(2560 - 380, rect.Left);
        Assert.Equal(1392 - 330, rect.Top);
    }

    [Fact]
    public void Card_offsets_are_scaled_by_dpi()
    {
        var scaled = Primary with { Scale = 1.5 };

        var rect = NotchLayout.PlaceCard(scaled, 100, 100, 380, 330);

        Assert.Equal(150, rect.Left);
        Assert.Equal(570, rect.Width);
        Assert.Equal((100.0, 100.0), NotchLayout.OffsetOf(rect, scaled));
    }

    [Theory]
    [InlineData(0, 0, 2560, 1440, true)]       // exact borderless fullscreen
    [InlineData(-8, -8, 2568, 1448, true)]     // maximised-style overhang
    [InlineData(0, 0, 2560, 1392, false)]      // maximised above the taskbar
    [InlineData(-1920, 0, 0, 1080, false)]     // fullscreen on the other monitor
    public void Covers_monitor(int l, int t, int r, int b, bool expected)
    {
        Assert.Equal(expected, NotchLayout.CoversMonitor(new ScreenRect(l, t, r, b), Primary.Bounds));
    }
}
