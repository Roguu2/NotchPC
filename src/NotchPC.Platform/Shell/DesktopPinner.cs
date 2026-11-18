using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NotchPC.Core.Layout;
using NotchPC.Platform.Native;

namespace NotchPC.Platform.Shell;

/// <summary>
/// Keeps widget windows "on the desktop": always directly above the desktop window (Progman) and below every
/// normal window, also after Win+D / clicking the desktop (which raises Progman to the top).
/// Tested on Windows 11 build 26300, where a child of Progman is not an option: Progman has
/// WS_EX_NOREDIRECTIONBITMAP, so per-pixel-alpha (UpdateLayeredWindow) children are never drawn.
/// Purely event-driven (WinEvent hooks), no polling.
/// </summary>
public sealed unsafe class DesktopPinner : IDisposable
{
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoZOrder = 0x0004;
    private const uint GwHwndPrev = 3;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;

    private static DesktopPinner? _instance;

    private readonly HashSet<IntPtr> _windows = [];
    private readonly List<IntPtr> _hooks = [];
    private readonly uint _taskbarCreated;
    private IntPtr _progman;

    public DesktopPinner()
    {
        if (_instance is not null)
        {
            throw new InvalidOperationException("Only one DesktopPinner can exist.");
        }

        _instance = this;
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _progman = FindProgman();
        _hooks.Add(Hook(EventSystemForeground, EventSystemForeground));
        _hooks.Add(Hook(EventSystemMoveSizeEnd, EventSystemMoveSizeEnd));
        _hooks.Add(Hook(EventSystemMinimizeStart, EventSystemMinimizeEnd));
    }

    /// <summary>Something that can change what covers the widgets happened (focus, minimise, move/resize).</summary>
    public event EventHandler? WindowsRearranged;

    /// <summary>
    /// The desktop got the focus (Win+D or a click on it). Windows raises the desktop window only after its
    /// show-desktop animation, so the owner should call <see cref="PushAllAboveDesktop"/> again a moment later.
    /// </summary>
    public event EventHandler? DesktopActivated;

    /// <summary>True while the desktop itself is the foreground window.</summary>
    public static bool DesktopIsForeground => IsDesktop(NativeMethods.GetForegroundWindow());

    /// <summary>Puts one pinned window on top of the other pinned windows (still below normal windows).</summary>
    public void BringToFrontOfPinned(IntPtr hwnd)
    {
        if (_windows.Contains(hwnd))
        {
            PushDown(hwnd);
        }
    }

    /// <summary>Puts every widget directly above the desktop window again.</summary>
    public void PushAllAboveDesktop()
    {
        foreach (var w in _windows)
        {
            PushDown(w);
        }
    }

    public void Register(IntPtr hwnd)
    {
        WindowHelper.MakeOverlay(hwnd, noActivate: true);
        _windows.Add(hwnd);
        PushDown(hwnd);
    }

    public void Unregister(IntPtr hwnd) => _windows.Remove(hwnd);

    /// <summary>Forward the widget's window messages here (from an HwndSource hook).</summary>
    public void HandleMessage(IntPtr hwnd, int msg, IntPtr lParam)
    {
        if (msg == WmWindowPosChanging && _windows.Contains(hwnd))
        {
            var pos = (WINDOWPOS*)lParam;
            if ((pos->flags & SwpNoZOrder) == 0)
            {
                // Whatever z-order was requested, sit directly above the desktop.
                var above = AboveDesktop();
                if (above == IntPtr.Zero)
                {
                    pos->flags |= SwpNoZOrder;
                }
                else
                {
                    pos->hwndInsertAfter = above;
                }
            }
        }
        else if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated)
        {
            // Explorer restarted: new desktop window.
            _progman = FindProgman();
            foreach (var w in _windows)
            {
                PushDown(w);
            }
        }
    }

    /// <summary>
    /// True if any of five sample points of the window is not covered by another window
    /// (centre and points near the corners). Cheap: five WindowFromPoint calls.
    /// </summary>
    public static bool IsUncovered(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd) || WindowHelper.GetBounds(hwnd) is not { } r || r.Width <= 0)
        {
            return false;
        }

        var inset = Math.Min(12, Math.Min(r.Width, r.Height) / 4);
        ReadOnlySpan<(int X, int Y)> points =
        [
            (r.Left + (r.Width / 2), r.Top + (r.Height / 2)),
            (r.Left + inset, r.Top + inset),
            (r.Right - inset, r.Top + inset),
            (r.Left + inset, r.Bottom - inset),
            (r.Right - inset, r.Bottom - inset),
        ];
        foreach (var (x, y) in points)
        {
            var hit = NativeMethods.WindowFromPoint(new POINT { X = x, Y = y });
            if (hit != IntPtr.Zero && NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT) == hwnd)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var h in _hooks)
        {
            if (h != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(h);
            }
        }

        _hooks.Clear();
        _windows.Clear();
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    private static IntPtr FindProgman() => NativeMethods.FindWindow("Progman", IntPtr.Zero);

    private static IntPtr Hook(uint min, uint max) =>
        NativeMethods.SetWinEventHook(min, max, IntPtr.Zero, &OnWinEvent, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            _instance?.OnEvent(evt, hwnd);
        }
#pragma warning disable CA1031 // An exception must never escape into native code.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void OnEvent(uint evt, IntPtr hwnd)
    {
        if (evt == EventSystemForeground && IsDesktop(hwnd))
        {
            // Win+D or a click on the desktop raises Progman above us: come back on top of it.
            PushAllAboveDesktop();
            DesktopActivated?.Invoke(this, EventArgs.Empty);
        }

        WindowsRearranged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var buffer = stackalloc char[32];
        var len = NativeMethods.GetClassName(hwnd, buffer, 32);
        var cls = new string(buffer, 0, len);
        return cls is "Progman" or "WorkerW";
    }

    /// <summary>Asks for a z-order change; the WM_WINDOWPOSCHANGING handler decides where exactly.</summary>
    private static void PushDown(IntPtr hwnd) =>
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    /// <summary>
    /// The first non-widget window above the desktop; inserting after it puts the widget directly above the
    /// desktop. After Win+D the desktop is at the top of the normal band, so this is the last topmost window
    /// and the widget lands at the top of the normal band (still not topmost).
    /// </summary>
    private IntPtr AboveDesktop()
    {
        if (_progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var above = NativeMethods.GetWindow(_progman, GwHwndPrev);
        while (above != IntPtr.Zero && _windows.Contains(above))
        {
            above = NativeMethods.GetWindow(above, GwHwndPrev);
        }

        return above;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPOS
{
    public IntPtr hwnd;
    public IntPtr hwndInsertAfter;
    public int x;
    public int y;
    public int cx;
    public int cy;
    public uint flags;
}
