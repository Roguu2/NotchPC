using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NotchPC.Platform.Audio;

namespace NotchPC.Platform.Shell;

/// <summary>
/// Brings an app forward by its AppUserModelID (what a Windows notification says about its sender): its open
/// window if there is one, otherwise the app is started like from the Start menu. Windows does not tell other
/// apps where a notification leads (its launch arguments), so this is as close as a click can get.
/// </summary>
public static partial class AppActivator
{
    private const int SwRestore = 9;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const int DwmwaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint AsfwAny = unchecked((uint)-1);
    private const ushort VtLpwstr = 31;

    private static readonly Dictionary<string, string?> TargetCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a window came forward or the app was started. Senders that are not apps (Windows system
    /// notifications such as "Windows.SystemToast.*") open the notification center instead.
    /// </summary>
    public static bool Activate(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        var hwnd = FindWindow(appUserModelId);
        if (hwnd != IntPtr.Zero)
        {
            if (IsIconic(hwnd))
            {
                _ = ShowWindow(hwnd, SwRestore);
            }

            if (SetForegroundWindow(hwnd))
            {
                return true;
            }
        }

        return IsStartable(appUserModelId) ? Start(@"shell:AppsFolder\" + appUserModelId) : OpenNotificationCenter();
    }

    /// <summary>The ID is an entry of the Start menu's app list (starting anything else would only show an error).</summary>
    internal static bool IsStartable(string aumid)
    {
        var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"); // IShellItem
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref iid, out var item) != 0 || item == IntPtr.Zero)
        {
            return false;
        }

        Marshal.Release(item);
        return true;
    }

    /// <summary>Opens the Windows notification center; clicking a notification there goes exactly where it leads.</summary>
    public static bool OpenNotificationCenter() => Start("ms-actioncenter:");

    /// <summary>Opens a web link in the default browser.</summary>
    public static bool OpenLink(Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Start(link.AbsoluteUri);
    }

    private static bool Start(string target)
    {
        try
        {
            _ = AllowSetForegroundWindow(AsfwAny); // let the started app take the foreground
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The app's frontmost normal window, or zero.</summary>
    internal static IntPtr FindWindow(string aumid)
    {
        var windows = AppWindows();

        // Windows that carry the ID themselves (Chrome, Edge, UWP frames) or belong to a packaged app with that ID.
        foreach (var (hwnd, pid) in windows)
        {
            if (string.Equals(WindowAumid(hwnd) ?? ProcessAumid(pid), aumid, StringComparison.OrdinalIgnoreCase))
            {
                return hwnd;
            }
        }

        // Classic desktop apps: same program as the Start menu shortcut with that ID.
        if (TargetOf(aumid) is { } target)
        {
            foreach (var (hwnd, pid) in windows)
            {
                if (SameProgram(ProcessPath(pid), target))
                {
                    return hwnd;
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The process is the shortcut's program, or lives in the shortcut target's folder (apps started through an
    /// updater, e.g. Discord's Update.exe → app-1.2\Discord.exe). Shared folders such as System32 need an exact match.
    /// </summary>
    internal static bool SameProgram(string? processPath, string target)
    {
        if (processPath is null)
        {
            return false;
        }

        if (string.Equals(processPath, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var folder = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(folder) || IsSharedFolder(folder))
        {
            return false;
        }

        return processPath.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSharedFolder(string folder)
    {
        var trimmed = folder.TrimEnd('\\');
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return trimmed.Length <= 3
            || trimmed.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Visible, unowned, titled, not cloaked windows of other processes, front to back.</summary>
    private static List<(IntPtr Hwnd, uint Pid)> AppWindows()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<(IntPtr, uint)>();
        EnumWindowsProc callback = (hwnd, lParam) =>
        {
            if (IsWindowVisible(hwnd)
                && GetWindow(hwnd, GwOwner) == IntPtr.Zero
                && (GetWindowLongPtrW(hwnd, GwlExStyle) & WsExToolWindow) == 0
                && GetWindowTextLengthW(hwnd) > 0
                && !IsCloaked(hwnd))
            {
                _ = GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != own)
                {
                    list.Add((hwnd, pid));
                }
            }

            return true;
        };
        _ = EnumWindows(Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string? WindowAumid(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var unknown) != 0 || unknown == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(unknown);
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 }; // System.AppUserModel.ID
            if (store.GetValue(ref key, out var value) != 0)
            {
                return null;
            }

            try
            {
                return value.VarType == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                _ = PropVariantClear(ref value);
                Marshal.ReleaseComObject(store);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static string? ProcessAumid(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            Span<char> buffer = stackalloc char[260];
            var length = (uint)buffer.Length;
            unsafe
            {
                fixed (char* p = buffer)
                {
                    return GetApplicationUserModelId(process, ref length, p) == 0 ? new string(buffer[..(int)Math.Max(0, length - 1)]) : null;
                }
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    public static string? ProcessPath(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            Span<char> buffer = stackalloc char[1024];
            var length = (uint)buffer.Length;
            unsafe
            {
                fixed (char* p = buffer)
                {
                    return QueryFullProcessImageNameW(process, 0, p, ref length) ? new string(buffer[..(int)length]) : null;
                }
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>The program behind a Start menu entry (System.Link.TargetParsingPath), or null for packaged apps.</summary>
    internal static string? TargetOf(string aumid)
    {
        lock (TargetCache)
        {
            if (TargetCache.TryGetValue(aumid, out var cached))
            {
                return cached;
            }
        }

        string? target = null;
        var iid = typeof(IShellItem2).GUID;
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref iid, out var unknown) == 0 && unknown != IntPtr.Zero)
        {
            try
            {
                var item = (IShellItem2)Marshal.GetObjectForIUnknown(unknown);
                var key = new PropertyKey { FormatId = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), PropertyId = 2 };
                if (item.GetString(ref key, out var text) == 0 && text != IntPtr.Zero)
                {
                    target = Marshal.PtrToStringUni(text);
                    Marshal.FreeCoTaskMem(text);
                }

                Marshal.ReleaseComObject(item);
            }
            catch (COMException)
            {
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        if (target is not null && !File.Exists(target))
        {
            target = null;
        }

        lock (TargetCache)
        {
            TargetCache[aumid] = target;
        }

        return target;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(IntPtr callback, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hwnd, uint command);

    [LibraryImport("user32.dll")]
    private static partial long GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IntPtr store);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IntPtr item);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetApplicationUserModelId(IntPtr process, ref uint length, char* buffer);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* buffer, ref uint length);

    // Only the vtable slots up to GetString are declared; the rest is never called.
    [ComImport]
    [Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        void BindToHandler();

        void GetParent();

        void GetDisplayName();

        void GetAttributes();

        void Compare();

        void GetPropertyStore();

        void GetPropertyStoreWithCreateObject();

        void GetPropertyStoreForKeys();

        void GetPropertyDescriptionList();

        void Update();

        void GetProperty();

        void GetCLSID();

        void GetFileTime();

        void GetInt32();

        [PreserveSig]
        int GetString(ref PropertyKey key, out IntPtr value);
    }
}
