using NotchPC.Core.Layout;
using NotchPC.Platform.Native;

namespace NotchPC.Platform.Shell;

/// <summary>Small Win32 helpers for overlay windows.</summary>
public static class WindowHelper
{
    /// <summary>
    /// Hides the window from Alt+Tab and the taskbar and stops it from taking focus when clicked
    /// (buttons still work, the game/app under it keeps keyboard focus).
    /// </summary>
    public static void MakeOverlay(IntPtr hwnd, bool noActivate)
    {
        var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW;
        ex &= ~NativeMethods.WS_EX_APPWINDOW;
        ex = noActivate ? ex | NativeMethods.WS_EX_NOACTIVATE : ex & ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>Moves/resizes the window in physical pixels and keeps it above normal windows.</summary>
    public static void PlaceTopmost(IntPtr hwnd, ScreenRect rect) =>
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, NativeMethods.SWP_NOACTIVATE);

    /// <summary>Moves/resizes the window in physical pixels without touching its z-order.</summary>
    public static void Place(IntPtr hwnd, ScreenRect rect) =>
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
            NativeMethods.SWP_NOACTIVATE | 0x0004 /* SWP_NOZORDER */);

    /// <summary>Re-asserts topmost without moving (other topmost windows may have been raised over us).</summary>
    public static void BringToTopmost(IntPtr hwnd) =>
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    public static IntPtr Foreground() => NativeMethods.GetForegroundWindow();

    public static uint ProcessIdOf(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>Gives focus to another window (allowed while our process is the foreground one).</summary>
    public static bool SetForeground(IntPtr hwnd) => NativeMethods.SetForegroundWindow(hwnd);

    /// <summary>Mouse position in physical pixels (virtual-screen coordinates).</summary>
    public static (int X, int Y) CursorPosition() =>
        NativeMethods.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>Adds WS_EX_TRANSPARENT: the window never receives mouse input (clicks go through).</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex | 0x00000020L));
    }

    public static ScreenRect? GetBounds(IntPtr hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var r) ? new ScreenRect(r.Left, r.Top, r.Right, r.Bottom) : null;
}
