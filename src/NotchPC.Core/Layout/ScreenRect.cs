namespace NotchPC.Core.Layout;

/// <summary>Rectangle in physical (device) pixels, virtual-screen coordinates.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public static ScreenRect FromSize(int left, int top, int width, int height) =>
        new(left, top, left + width, top + height);

    public bool Contains(ScreenRect other) =>
        other.Left >= Left && other.Top >= Top && other.Right <= Right && other.Bottom <= Bottom;

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}

/// <summary>A display as seen by the notch: physical bounds and its DPI scale (1.0 = 96 DPI).</summary>
public sealed record MonitorDescriptor(string DeviceName, ScreenRect Bounds, ScreenRect WorkArea, double Scale, bool IsPrimary);
