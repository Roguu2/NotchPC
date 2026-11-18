using System.Runtime.InteropServices;
using NotchPC.Core.Layout;

namespace NotchPC.Platform.Shell;

/// <summary>The taskbar on a monitor: where it is and where the notification area (clock, tray icons) starts.</summary>
/// <param name="TrayLeft">Left edge of the notification area, when Windows exposes it (primary taskbar).</param>
public sealed record TaskbarInfo(ScreenRect Bounds, int? TrayLeft, bool AutoHide)
{
    public bool IsBottom(ScreenRect monitor) => Bounds.Bottom >= monitor.Bottom - 2 && Bounds.Width >= monitor.Width - 2;
}

/// <summary>
/// Finds the Windows 11 taskbar windows (Shell_TrayWnd / Shell_SecondaryTrayWnd). Windows 11 has no API to put
/// something into the taskbar (no deskbands), so a window can only be laid over it.
/// </summary>
public static partial class Taskbar
{
    private const uint AbmGetState = 4;
    private const int AbsAutoHide = 1;

    public static TaskbarInfo? On(ScreenRect monitor)
    {
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var hwnd = IntPtr.Zero;
            while ((hwnd = FindWindowExW(IntPtr.Zero, hwnd, cls, null)) != IntPtr.Zero)
            {
                if (!GetWindowRect(hwnd, out var r))
                {
                    continue;
                }

                var rect = new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
                var cx = (rect.Left + rect.Right) / 2;
                var cy = (rect.Top + rect.Bottom) / 2;
                if (cx < monitor.Left || cx >= monitor.Right || cy < monitor.Top || cy >= monitor.Bottom)
                {
                    continue;
                }

                int? trayLeft = null;
                var tray = FindWindowExW(hwnd, IntPtr.Zero, "TrayNotifyWnd", null);
                if (tray != IntPtr.Zero && GetWindowRect(tray, out var t) && t.Right > t.Left)
                {
                    trayLeft = t.Left;
                }

                var autoHide = cls == "Shell_TrayWnd" && (AutoHideState() & AbsAutoHide) != 0;
                return new TaskbarInfo(rect, trayLeft, autoHide);
            }
        }

        return null;
    }

    private static int AutoHideState()
    {
        var data = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        return (int)SHAppBarMessage(AbmGetState, ref data);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string? title);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Rect;
        public IntPtr Param;
    }
}
