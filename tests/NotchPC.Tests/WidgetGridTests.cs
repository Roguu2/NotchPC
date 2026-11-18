using NotchPC.Core.Layout;

namespace NotchPC.Tests;

public sealed class WidgetGridTests
{
    private static readonly GridSpec Grid = new(16, 24);
    private static readonly RectD Work = new(0, 0, 1920, 1032); // 1080p minus a 48 px taskbar

    [Theory]
    [InlineData(31, 24, 16, 24)]
    [InlineData(33, 24, 16, 40)]
    [InlineData(-5, 0, 10, -10)]
    public void Snaps_coordinates_to_the_nearest_line(double value, double origin, double cell, double expected)
    {
        Assert.Equal(expected, WidgetGrid.SnapCoordinate(value, origin, cell));
    }

    [Theory]
    [InlineData(230, 16, 100, 224)]
    [InlineData(239, 16, 100, 240)]
    [InlineData(40, 16, 100, 112)] // never below the minimum, rounded up to whole cells
    public void Snaps_lengths(double value, double cell, double min, double expected)
    {
        Assert.Equal(expected, WidgetGrid.SnapLength(value, cell, min));
    }

    [Fact]
    public void Position_is_snapped_and_kept_inside_the_margin()
    {
        var r = WidgetGrid.SnapPosition(new RectD(1900, -50, 224, 96), Grid, Work);

        Assert.Equal(24, r.Y);
        Assert.True(r.Right <= Work.Right - Grid.Margin);
        Assert.Equal(0, (r.X - 24) % 16);
    }

    [Fact]
    public void Free_spot_is_the_desired_one_when_nothing_is_in_the_way()
    {
        var r = WidgetGrid.FindFreeSpot(new RectD(100, 100, 224, 96), [new RectD(600, 600, 224, 96)], Grid, Work);

        Assert.Equal((104, 104), (r.X, r.Y));
    }

    [Fact]
    public void Overlapping_drop_moves_to_the_nearest_free_cell()
    {
        var other = new RectD(104, 104, 224, 96);

        var r = WidgetGrid.FindFreeSpot(new RectD(120, 120, 224, 96), [other], Grid, Work);

        Assert.False(r.Overlaps(other));
        // Nearest free cell is directly below (shorter move than to the right).
        Assert.Equal((120, 200), (r.X, r.Y));
    }

    [Fact]
    public void Gap_keeps_widgets_apart_on_the_next_grid_line()
    {
        var other = new RectD(104, 104, 224, 96); // bottom edge at 200

        var r = WidgetGrid.FindFreeSpot(new RectD(120, 120, 224, 96), [other], Grid, Work, gap: 12);

        Assert.Equal((120, 216), (r.X, r.Y)); // 200 + 12 → next line 216
        Assert.False(r.Overlaps(other, 12));
    }

    [Fact]
    public void Touching_edges_do_not_count_as_overlap()
    {
        Assert.False(new RectD(0, 0, 100, 100).Overlaps(new RectD(100, 0, 100, 100)));
        Assert.True(new RectD(0, 0, 100, 100).Overlaps(new RectD(99, 0, 100, 100)));
    }
}
