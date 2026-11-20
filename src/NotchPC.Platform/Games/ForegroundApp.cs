using System.Runtime.InteropServices;
using NotchPC.Core.Layout;
using NotchPC.Platform.Monitors;
using NotchPC.Platform.Native;

namespace NotchPC.Platform.Games;

/// <summary>The app in front: its process, its monitor and whether its window covers that monitor (fullscreen).</summary>
public sealed partial record ForegroundApp(IntPtr Hwnd, uint ProcessId, string ProcessName, MonitorDescriptor Monitor, bool CoversMonitor)
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>Null for the desktop, the taskbar, NotchPC itself or when nothing is in front.</summary>
    public static unsafe ForegroundApp? Current()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return null;
        }

        var buffer = stackalloc char[256];
        var length = NativeMethods.GetClassName(hwnd, buffer, 256);
        if (length > 0 && ShellClasses.Contains(new string(buffer, 0, length)))
        {
            return null;
        }

        var device = MonitorService.DeviceNameOf(hwnd);
        var monitor = MonitorService.GetMonitors().FirstOrDefault(m => m.DeviceName == device);
        if (monitor is null || !NativeMethods.GetWindowRect(hwnd, out var r) || ReadProcessName(pid) is not { } name)
        {
            return null;
        }

        var rect = MonitorService.ToRect(r);
        var covers = NotchLayout.CoversMonitor(rect, monitor.Bounds) && !Shell.WindowHelper.IsMaximizedAppWindow(hwnd);
        return new ForegroundApp(hwnd, pid, name, monitor, covers);
    }

    /// <summary>"Overwatch" for …\Overwatch.exe; null when the process cannot be opened (e.g. protected).</summary>
    private static unsafe string? ReadProcessName(uint pid)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, 0, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var path = stackalloc char[1024];
            var size = 1024u;
            return QueryFullProcessImageNameW(process, 0, path, &size) != 0
                ? Path.GetFileNameWithoutExtension(new string(path, 0, (int)size))
                : null;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(uint access, int inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int QueryFullProcessImageNameW(IntPtr process, uint flags, char* buffer, uint* size);
}
