using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NotchPC.Core.Layout;
using NotchPC.Platform.Monitors;
using NotchPC.Platform.Native;

namespace NotchPC.Platform.Shell;

/// <summary>
/// Tells whether a fullscreen app (game, video, F11 browser) covers the notch's monitor.
/// Event-driven only: the shell's ABN_FULLSCREENAPP appbar notification plus a foreground-change WinEvent hook.
/// Nothing is polled.
/// </summary>
public sealed unsafe class FullscreenWatcher : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private static FullscreenWatcher? _instance;

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private IntPtr _hwnd;
    private IntPtr _hook;
    private uint _callbackMessage;
    private bool _shellSaysFullscreen;
    private ScreenRect? _target;

    public event EventHandler? Changed;

    /// <summary>The foreground window changed or the shell reported fullscreen (raised every time, changed or not).</summary>
    public event EventHandler? Evaluated;

    public bool IsFullscreen { get; private set; }

    /// <summary>Optional diagnostics sink (state changes and swallowed callback errors).</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Starts listening. <paramref name="hwnd"/> receives the appbar callback message (forward it to <see cref="TryHandleMessage"/>).</summary>
    public void Attach(IntPtr hwnd)
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        if (_instance is not null)
        {
            throw new InvalidOperationException("Only one FullscreenWatcher can be attached.");
        }

        _instance = this;
        _hwnd = hwnd;
        _callbackMessage = NativeMethods.RegisterWindowMessage("NotchPC.AppBarCallback");

        var abd = NewAppBarData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref abd);

        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            &OnWinEvent,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>Sets the monitor (physical bounds) the notch lives on and re-evaluates.</summary>
    public void SetTargetMonitor(ScreenRect bounds)
    {
        _target = bounds;
        Evaluate();
    }

    /// <returns>True when the message was the appbar callback (already handled).</returns>
    public bool TryHandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_callbackMessage == 0 || (uint)msg != _callbackMessage)
        {
            return false;
        }

        if (wParam.ToInt64() == NativeMethods.ABN_FULLSCREENAPP)
        {
            _shellSaysFullscreen = lParam != IntPtr.Zero;
            Evaluate();
        }

        return true;
    }

    public void Evaluate()
    {
        var now = ComputeFullscreen();
        Evaluated?.Invoke(this, EventArgs.Empty);
        if (now == IsFullscreen)
        {
            return;
        }

        IsFullscreen = now;
        Trace?.Invoke($"Fullscreen on notch monitor: {now} (shell flag {_shellSaysFullscreen})");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            var abd = NewAppBarData();
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref abd);
            _hwnd = IntPtr.Zero;
        }

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    private bool ComputeFullscreen()
    {
        if (_target is not { } target)
        {
            return false;
        }

        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || !NativeMethods.IsWindowVisible(fg))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(fg, out var pid);
        if (pid == _ownProcessId || ShellClasses.Contains(ClassName(fg)))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(fg, out var r))
        {
            return false;
        }

        var rect = MonitorService.ToRect(r);
        if (NotchLayout.CoversMonitor(rect, target))
        {
            // A maximised app on a monitor with an auto-hidden (or no) taskbar also covers it: not fullscreen.
            return !WindowHelper.IsMaximizedAppWindow(fg);
        }

        // Exclusive-fullscreen games can report odd window rects; trust the shell if the
        // foreground window at least sits on our monitor.
        return _shellSaysFullscreen && MonitorService.DeviceNameOf(fg) is { } dev &&
               MonitorService.GetMonitors().Any(m => m.DeviceName == dev && m.Bounds == target);
    }

    private APPBARDATA NewAppBarData() => new()
    {
        cbSize = (uint)sizeof(APPBARDATA),
        hWnd = _hwnd,
        uCallbackMessage = _callbackMessage,
    };

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = stackalloc char[256];
        var len = NativeMethods.GetClassName(hwnd, buffer, 256);
        return len > 0 ? new string(buffer, 0, len) : "";
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            _instance?.Evaluate();
        }
#pragma warning disable CA1031 // An exception must never escape into native code.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _instance?.Trace?.Invoke($"Foreground hook failed: {ex}");
        }
    }
}
