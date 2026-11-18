using System.Runtime.InteropServices;
using NotchPC.Core.Display;

namespace NotchPC.Platform.Monitors;

/// <summary>
/// Brightness of external monitors over DDC/CI (dxva2, no admin). It has to be enabled in the monitor's own menu;
/// some monitors, adapters and docks do not pass it through. Every call talks to the monitor over the cable and
/// takes 40–100+ ms, so callers must stay off the UI thread and send only the last slider value.
/// </summary>
public static partial class MonitorBrightness
{
    /// <summary>Brightness 0..100 of the first monitor that answers, or null when none supports DDC/CI.</summary>
    public static int? Read()
    {
        int? value = null;
        ForEachPhysical(handle =>
        {
            if (GetMonitorBrightness(handle, out var min, out var current, out var max) &&
                BrightnessScale.ToPercent(min, current, max) is { } percent)
            {
                value = percent;
                return false; // stop
            }

            return true;
        });
        return value;
    }

    /// <summary>Sets 0..100 on every monitor that supports it. Returns how many monitors accepted it.</summary>
    public static int Write(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var done = 0;
        ForEachPhysical(handle =>
        {
            if (GetMonitorBrightness(handle, out var min, out _, out var max) && max > min &&
                SetMonitorBrightness(handle, BrightnessScale.FromPercent(percent, min, max)))
            {
                done++;
            }

            return true;
        });
        return done;
    }

    /// <summary>Calls <paramref name="action"/> for every physical monitor until it returns false.</summary>
    private static void ForEachPhysical(Func<IntPtr, bool> action)
    {
        foreach (var hMonitor in MonitorService.GetHandles())
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
            {
                continue;
            }

            var monitors = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
            {
                continue;
            }

            try
            {
                foreach (var m in monitors)
                {
                    if (!action(m.Handle))
                    {
                        return;
                    }
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, monitors);
            }
        }
    }

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] monitors);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMonitorBrightness(IntPtr monitor, uint value);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PhysicalMonitor
    {
        public IntPtr Handle;
        public fixed char Description[128];
    }
}
