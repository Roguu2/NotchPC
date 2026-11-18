namespace NotchPC.Core.Layout;

/// <summary>Rectangle in DIPs (relative to a monitor's top-left).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>True if the two rectangles share any area (touching edges do not count).</summary>
    public bool Overlaps(RectD other) => Overlaps(other, 0);

    /// <summary>True if the rectangles are closer than <paramref name="gap"/> (or overlap).</summary>
    public bool Overlaps(RectD other, double gap) =>
        X < other.Right + gap - 0.5 && other.X < Right + gap - 0.5 && Y < other.Bottom + gap - 0.5 && other.Y < Bottom + gap - 0.5;
}

/// <summary>Snap grid for desktop widgets: cell size and the margin from the work-area edges (DIPs).</summary>
public sealed record GridSpec(double Cell, double Margin);

/// <summary>Pure grid maths for the "grid" widget layout mode (unit-tested).</summary>
public static class WidgetGrid
{
    /// <summary>Nearest grid line to <paramref name="value"/> for a grid starting at <paramref name="origin"/>.</summary>
    public static double SnapCoordinate(double value, double origin, double cell) =>
        origin + (Math.Round((value - origin) / cell, MidpointRounding.AwayFromZero) * cell);

    /// <summary>Length rounded to whole cells, never below <paramref name="min"/> (rounded up to a whole cell).</summary>
    public static double SnapLength(double value, double cell, double min)
    {
        var minCells = Math.Ceiling(min / cell);
        return Math.Max(minCells, Math.Round(value / cell, MidpointRounding.AwayFromZero)) * cell;
    }

    /// <summary>
    /// Snaps the top-left of <paramref name="rect"/> to the grid and keeps the rectangle inside the work area
    /// (minus the margin), staying on grid lines. The size is not changed.
    /// </summary>
    public static RectD SnapPosition(RectD rect, GridSpec grid, RectD workArea)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var originX = workArea.X + grid.Margin;
        var originY = workArea.Y + grid.Margin;
        var maxX = LastLine(originX, workArea.Right - grid.Margin - rect.Width, grid.Cell);
        var maxY = LastLine(originY, workArea.Bottom - grid.Margin - rect.Height, grid.Cell);
        var x = Math.Clamp(SnapCoordinate(rect.X, originX, grid.Cell), originX, Math.Max(originX, maxX));
        var y = Math.Clamp(SnapCoordinate(rect.Y, originY, grid.Cell), originY, Math.Max(originY, maxY));
        return rect with { X = x, Y = y };
    }

    /// <summary>
    /// The grid position closest to <paramref name="desired"/> where the rectangle overlaps none of
    /// <paramref name="others"/>; <paramref name="desired"/> itself (snapped) if it is already free.
    /// Falls back to the snapped desired position when the screen is full. <paramref name="gap"/> = minimum
    /// space kept between rectangles.
    /// </summary>
    public static RectD FindFreeSpot(RectD desired, IReadOnlyCollection<RectD> others, GridSpec grid, RectD workArea, double gap = 0)
    {
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(grid);
        var start = SnapPosition(desired, grid, workArea);
        if (others.All(o => !start.Overlaps(o, gap)))
        {
            return start;
        }

        var originX = workArea.X + grid.Margin;
        var originY = workArea.Y + grid.Margin;
        var maxX = Math.Max(originX, LastLine(originX, workArea.Right - grid.Margin - desired.Width, grid.Cell));
        var maxY = Math.Max(originY, LastLine(originY, workArea.Bottom - grid.Margin - desired.Height, grid.Cell));

        RectD? best = null;
        var bestDistance = double.MaxValue;
        for (var y = originY; y <= maxY + 0.01; y += grid.Cell)
        {
            for (var x = originX; x <= maxX + 0.01; x += grid.Cell)
            {
                var candidate = desired with { X = x, Y = y };
                var dx = x - start.X;
                var dy = y - start.Y;
                var distance = (dx * dx) + (dy * dy);
                if (distance < bestDistance && others.All(o => !candidate.Overlaps(o, gap)))
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
        }

        return best ?? start;
    }

    private static double LastLine(double origin, double limit, double cell) =>
        origin + (Math.Floor((limit - origin) / cell) * cell);
}
