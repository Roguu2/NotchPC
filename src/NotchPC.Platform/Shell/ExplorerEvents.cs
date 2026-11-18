using System.Runtime.InteropServices;

namespace NotchPC.Platform.Shell;

/// <summary>
/// Tells when the shell (explorer.exe: taskbar, tray) changes the z-order or moves one of its windows, e.g. the
/// taskbar lifting itself over a window lying on it after a click, or the tray growing when an icon appears.
/// Event-driven (WinEvent hooks limited to explorer's process); window-level events only.
/// </summary>
public sealed partial class ExplorerEvents : IDisposable
{
    private const uint EventObjectReorder = 0x8004;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;

    private readonly WinEventProc _callback;
    private readonly List<IntPtr> _hooks = [];

    public ExplorerEvents()
    {
        _callback = OnEvent; // kept alive for as long as the hooks exist
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || GetWindowThreadProcessId(tray, out var pid) == 0 || pid == 0)
        {
            return;
        }

        var proc = Marshal.GetFunctionPointerForDelegate(_callback);
        foreach (var evt in new[] { EventObjectReorder, EventObjectLocationChange })
        {
            var hook = SetWinEventHook(evt, evt, IntPtr.Zero, proc, pid, 0, WinEventOutOfContext);
            if (hook != IntPtr.Zero)
            {
                _hooks.Add(hook);
            }
        }
    }

    /// <summary>Raised on the thread that created this object (it must pump messages).</summary>
    public event EventHandler? Changed;

    public bool IsActive => _hooks.Count > 0;

    public void Dispose()
    {
        foreach (var hook in _hooks)
        {
            _ = UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject == ObjIdWindow && idChild == 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string className, string? title);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetWinEventHook(uint min, uint max, IntPtr module, IntPtr callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(IntPtr hook);
}
