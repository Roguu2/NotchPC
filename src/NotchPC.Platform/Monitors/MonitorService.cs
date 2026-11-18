using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NotchPC.Core.Layout;
using NotchPC.Platform.Native;

namespace NotchPC.Platform.Monitors;

/// <summary>Enumerates displays with their physical bounds and effective DPI scale.</summary>
public static unsafe class MonitorService
{
    public static IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var result = new List<MonitorDescriptor>();
        foreach (var h in GetHandles())
        {
            if (TryDescribe(h) is { } d)
            {
                result.Add(d);
            }
        }

        return result;
    }

    /// <summary>HMONITOR handles of all displays.</summary>
    internal static List<IntPtr> GetHandles()
    {
        var handles = new List<IntPtr>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &OnMonitor, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        return handles;
    }

    /// <summary>Monitor handle that contains the largest part of the window (null if none).</summary>
    public static IntPtr MonitorOf(IntPtr hwnd) => NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONULL);

    /// <summary>Device name (\\.\DISPLAYn) of the monitor showing most of the window.</summary>
    public static string? DeviceNameOf(IntPtr hwnd)
    {
        var h = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONULL);
        return h == IntPtr.Zero ? null : TryDescribe(h)?.DeviceName;
    }

    internal static MonitorDescriptor? TryDescribe(IntPtr hMonitor)
    {
        var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            return null;
        }

        var name = new string(info.szDevice);
        var scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        return new MonitorDescriptor(
            name.TrimEnd('\0'),
            ToRect(info.rcMonitor),
            ToRect(info.rcWork),
            scale,
            (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0);
    }

    internal static ScreenRect ToRect(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnMonitor(IntPtr hMonitor, IntPtr hdc, RECT* rect, IntPtr data)
    {
        if (GCHandle.FromIntPtr(data).Target is List<IntPtr> list)
        {
            list.Add(hMonitor);
        }

        return 1;
    }
}
