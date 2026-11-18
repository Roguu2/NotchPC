using NotchPC.Platform.Native;

namespace NotchPC.Platform.Shell;

/// <summary>
/// Registers a window for WM_CLIPBOARDUPDATE (AddClipboardFormatListener): Windows tells us when the clipboard
/// changes, nothing is polled. The owner forwards the window's messages to <see cref="IsClipboardUpdate"/>.
/// </summary>
public static class ClipboardListener
{
    public const int WmClipboardUpdate = 0x031D;

    public static bool Start(IntPtr hwnd) => NativeMethods.AddClipboardFormatListener(hwnd);

    public static void Stop(IntPtr hwnd) => NativeMethods.RemoveClipboardFormatListener(hwnd);

    public static bool IsClipboardUpdate(int msg) => msg == WmClipboardUpdate;
}
