namespace NotchPC.Core.Layout;

/// <summary>Pure placement logic for the notch window (kept free of Win32 so it can be unit-tested).</summary>
public static class NotchLayout
{
    /// <summary>
    /// Picks the monitor named in settings, falling back to the primary one
    /// (and to the first one if Windows reports no primary, e.g. mid-reconfiguration).
    /// </summary>
    public static MonitorDescriptor? SelectMonitor(IReadOnlyList<MonitorDescriptor> monitors, string? preferredDeviceName)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(preferredDeviceName))
        {
            foreach (var m in monitors)
            {
                if (string.Equals(m.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return m;
                }
            }
        }

        foreach (var m in monitors)
        {
            if (m.IsPrimary)
            {
                return m;
            }
        }

        return monitors[0];
    }

    /// <summary>
    /// Physical rectangle of a window that is <paramref name="widthDip"/> x <paramref name="heightDip"/> DIPs,
    /// horizontally centred on the monitor and glued to its top edge.
    /// </summary>
    public static ScreenRect TopCenter(MonitorDescriptor monitor, double widthDip, double heightDip, double offsetDip = 0)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var w = (int)Math.Round(widthDip * monitor.Scale);
        var h = (int)Math.Round(heightDip * monitor.Scale);
        var left = monitor.Bounds.Left + ((monitor.Bounds.Width - w) / 2) + (int)Math.Round(offsetDip * monitor.Scale);
        left = Math.Clamp(left, monitor.Bounds.Left, Math.Max(monitor.Bounds.Left, monitor.Bounds.Right - w));
        return ScreenRect.FromSize(left, monitor.Bounds.Top, w, h);
    }

    /// <summary>
    /// Keeps a moved notch on its monitor: the widest window it uses (the expanded canvas) must fit, so the notch
    /// looks the same open and closed.
    /// </summary>
    public static double ClampOffset(MonitorDescriptor monitor, double offsetDip, double widestDip)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var max = Math.Max(0, ((monitor.Bounds.Width / monitor.Scale) - widestDip) / 2);
        return Math.Clamp(offsetDip, -max, max);
    }

    /// <summary>Horizontal centre of the notch in physical pixels.</summary>
    public static int CenterX(MonitorDescriptor monitor, double offsetDip)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return monitor.Bounds.Left + (monitor.Bounds.Width / 2) + (int)Math.Round(offsetDip * monitor.Scale);
    }

    /// <summary>
    /// Physical rectangle of a card stored as a DIP offset from a monitor's top-left, pulled back inside the
    /// monitor's work area (so a card saved on a bigger screen or above the taskbar never ends up unreachable).
    /// </summary>
    public static ScreenRect PlaceCard(MonitorDescriptor monitor, double xDip, double yDip, double widthDip, double heightDip)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var w = (int)Math.Round(widthDip * monitor.Scale);
        var h = (int)Math.Round(heightDip * monitor.Scale);
        var work = monitor.WorkArea;
        var left = monitor.Bounds.Left + (int)Math.Round(xDip * monitor.Scale);
        var top = monitor.Bounds.Top + (int)Math.Round(yDip * monitor.Scale);
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - w));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h));
        return ScreenRect.FromSize(left, top, w, h);
    }

    /// <summary>Inverse of <see cref="PlaceCard"/>: DIP offset of a window's top-left from its monitor's top-left.</summary>
    public static (double X, double Y) OffsetOf(ScreenRect window, MonitorDescriptor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ((window.Left - monitor.Bounds.Left) / monitor.Scale, (window.Top - monitor.Bounds.Top) / monitor.Scale);
    }

    /// <summary>
    /// True when a window covers the whole monitor (borderless fullscreen, F11, video players, games).
    /// A couple of pixels of tolerance covers windows that hang slightly off-screen.
    /// </summary>
    public static bool CoversMonitor(ScreenRect window, ScreenRect monitor, int tolerancePx = 2) =>
        window.Left <= monitor.Left + tolerancePx &&
        window.Top <= monitor.Top + tolerancePx &&
        window.Right >= monitor.Right - tolerancePx &&
        window.Bottom >= monitor.Bottom - tolerancePx;
}
