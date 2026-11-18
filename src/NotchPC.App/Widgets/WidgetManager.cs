using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Layout;
using NotchPC.Core.Settings;
using NotchPC.Platform.Monitors;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Widgets;

public static class WidgetIds
{
    public const string Timer = "timer";
    public const string Weather = "weather";
    public const string Calendar = "calendar";
    public const string Stats = "stats";
    public const string Twitch = "twitch";

    public static IReadOnlyList<string> DefaultOrder { get; } = [Timer, Weather, Calendar, Stats, Twitch];
}

/// <summary>
/// Creates/destroys the desktop widgets according to settings and lays them out in one of two modes:
/// free (anywhere, any size) or grid (snapped to cells, no overlaps, dotted grid shown while moving).
/// Also tracks whether each widget can actually be seen; visibility is re-checked on window events
/// (focus, minimise, move/resize, Win+D, lock), never on a timer.
/// </summary>
internal sealed class WidgetManager : IWidgetLayoutHost, IDisposable
{
    private const double DefaultTop = 110;
    private const double DefaultRightMargin = 28;
    private const double StackGap = 12;
    private const double MinWidth = 200;
    private const double MaxWidth = 640;
    private const double MaxHeight = 640;
    private const double GridGap = 12; // minimum space between widgets in grid mode

    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly SensorsClient _sensors;
    private readonly DesktopPinner _pinner = new();
    private readonly Dictionary<string, DesktopWidgetWindow> _windows = [];
    private readonly DispatcherTimer _recheck;
    private readonly DispatcherTimer _desktopFollowUp;
    private static readonly int[] FollowUpDelaysMs = [350, 900, 1600, 2500];
    private GridOverlayWindow? _overlay;
    private int _followUpStep;
    private bool _sessionLocked;

    public WidgetManager(SettingsService settings, HttpClient http, SensorsClient sensors)
    {
        _settings = settings;
        _http = http;
        _sensors = sensors;

        // Windows animate when minimising/restoring: check a moment after the event, coalescing bursts.
        _recheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _recheck.Tick += (_, _) =>
        {
            _recheck.Stop();
            RecheckAll();
        };
        _pinner.WindowsRearranged += (_, _) => ScheduleRecheck();

        // Show-desktop raises the desktop window after its animation: push the widgets above it again
        // a few times over the next seconds (its timing varies; nothing runs otherwise).
        _desktopFollowUp = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _desktopFollowUp.Tick += (_, _) =>
        {
            _pinner.PushAllAboveDesktop();
            if (++_followUpStep < FollowUpDelaysMs.Length)
            {
                _desktopFollowUp!.Interval = TimeSpan.FromMilliseconds(FollowUpDelaysMs[_followUpStep] - FollowUpDelaysMs[_followUpStep - 1]);
            }
            else
            {
                _desktopFollowUp!.Stop();
            }

            ScheduleRecheck();
        };
        _pinner.DesktopActivated += (_, _) =>
        {
            _desktopFollowUp.Stop();
            _followUpStep = 0;
            _desktopFollowUp.Interval = TimeSpan.FromMilliseconds(FollowUpDelaysMs[0]);
            _desktopFollowUp.Start();
        };

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _settings.Changed += (_, _) => Sync();
    }

    public bool IsLocked => _settings.Current.Widgets.Locked;

    private WidgetSettings W => _settings.Current.Widgets;

    private bool IsGrid => W.LayoutMode == WidgetLayoutModes.Grid;

    private GridSpec Grid => new(W.GridCell, W.GridMargin);

    public void Start() => Sync();

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _recheck.Stop();
        _desktopFollowUp.Stop();
        foreach (var w in _windows.Values.ToList())
        {
            CloseWidget(w);
        }

        _overlay?.Close();
        _pinner.Dispose();
    }

    // ------------------------------------------------------------------ IWidgetLayoutHost

    public WidgetPlacement ConstrainMove(DesktopWidgetWindow window, ScreenRect proposed)
    {
        var monitor = MonitorAt(proposed);
        var work = WorkAreaDip(monitor);
        var rect = new RectD(
            (proposed.Left - monitor.Bounds.Left) / monitor.Scale,
            (proposed.Top - monitor.Bounds.Top) / monitor.Scale,
            window.WidthDip,
            window.HeightDip);

        if (IsGrid)
        {
            rect = WidgetGrid.SnapPosition(rect, Grid, work);
            ShowOverlay(monitor); // follows the widget to another monitor
        }
        else
        {
            rect = KeepInside(rect, work);
        }

        return ToPlacement(monitor, rect);
    }

    public WidgetPlacement ConstrainResize(DesktopWidgetWindow window, ScreenRect proposed)
    {
        var monitor = MonitorAt(window.Bounds ?? proposed);
        var work = WorkAreaDip(monitor);
        var x = (proposed.Left - monitor.Bounds.Left) / monitor.Scale;
        var y = (proposed.Top - monitor.Bounds.Top) / monitor.Scale;
        var (w, h) = ClampSize(window, proposed.Width / monitor.Scale, proposed.Height / monitor.Scale);
        if (IsGrid)
        {
            w = WidgetGrid.SnapLength(w, Grid.Cell, MinWidth);
            h = WidgetGrid.SnapLength(h, Grid.Cell, window.NaturalHeight);
        }

        // Never grow past the work area (top-left stays put while resizing).
        w = Math.Min(w, Math.Max(MinWidth, work.Right - (IsGrid ? Grid.Margin : 0) - x));
        h = Math.Min(h, Math.Max(window.NaturalHeight, work.Bottom - (IsGrid ? Grid.Margin : 0) - y));
        return ToPlacement(monitor, new RectD(x, y, w, h));
    }

    public void InteractionStarted(DesktopWidgetWindow window)
    {
        if (IsGrid && window.Bounds is { } r)
        {
            ShowOverlay(MonitorAt(r));
        }

        _pinner.BringToFrontOfPinned(new WindowInteropHelper(window).Handle);
    }

    public void InteractionEnded(DesktopWidgetWindow window)
    {
        _overlay?.HideOverlay();
        if (window.Bounds is not { } r)
        {
            return;
        }

        var monitor = MonitorAt(r);
        var rect = new RectD((r.Left - monitor.Bounds.Left) / monitor.Scale, (r.Top - monitor.Bounds.Top) / monitor.Scale, window.WidthDip, window.HeightDip);
        if (IsGrid)
        {
            // Dropped on another widget: slide to the nearest free cells.
            rect = WidgetGrid.FindFreeSpot(rect, OthersOn(monitor, window), Grid, WorkAreaDip(monitor), GridGap);
            window.Place(ToPlacement(monitor, rect));
        }

        var natural = Math.Abs(window.WidthDip - DesktopWidgetWindow.NaturalWidth) < 0.5 && Math.Abs(window.HeightDip - window.NaturalHeight) < 0.5;
        _settings.Update(s => s.Widgets.Positions[window.Id] = new CardPosition
        {
            Monitor = monitor.DeviceName,
            X = rect.X,
            Y = rect.Y,
            Width = natural ? null : rect.Width,
            Height = natural ? null : rect.Height,
        });
        ScheduleRecheck();
    }

    // ------------------------------------------------------------------ layout

    private static bool Enabled(WidgetSettings w, string id) => id switch
    {
        WidgetIds.Timer => w.TimerEnabled,
        WidgetIds.Weather => w.WeatherEnabled,
        WidgetIds.Calendar => w.CalendarEnabled,
        WidgetIds.Stats => w.StatsEnabled,
        WidgetIds.Twitch => w.TwitchEnabled,
        _ => false,
    };

    private static RectD WorkAreaDip(MonitorDescriptor m) => new(
        (m.WorkArea.Left - m.Bounds.Left) / m.Scale,
        (m.WorkArea.Top - m.Bounds.Top) / m.Scale,
        m.WorkArea.Width / m.Scale,
        m.WorkArea.Height / m.Scale);

    private static RectD KeepInside(RectD r, RectD work) => r with
    {
        X = Math.Clamp(r.X, work.X, Math.Max(work.X, work.Right - r.Width)),
        Y = Math.Clamp(r.Y, work.Y, Math.Max(work.Y, work.Bottom - r.Height)),
    };

    private static WidgetPlacement ToPlacement(MonitorDescriptor m, RectD r) => new(
        ScreenRect.FromSize(
            m.Bounds.Left + (int)Math.Round(r.X * m.Scale),
            m.Bounds.Top + (int)Math.Round(r.Y * m.Scale),
            (int)Math.Round(r.Width * m.Scale),
            (int)Math.Round(r.Height * m.Scale)),
        r.Width,
        r.Height);

    private static (double W, double H) ClampSize(DesktopWidgetWindow window, double w, double h)
    {
        // Never smaller than the compact layout; bigger cards show more data.
        h = Math.Clamp(h, window.NaturalHeight, Math.Max(window.NaturalHeight, MaxHeight));
        return (Math.Clamp(w, MinWidth, MaxWidth), h);
    }

    private static MonitorDescriptor MonitorAt(ScreenRect r)
    {
        var monitors = MonitorService.GetMonitors();
        var cx = r.Left + (r.Width / 2);
        var cy = r.Top + (r.Height / 2);
        return monitors.FirstOrDefault(m => cx >= m.Bounds.Left && cx < m.Bounds.Right && cy >= m.Bounds.Top && cy < m.Bounds.Bottom)
               ?? NotchLayout.SelectMonitor(monitors, null)
               ?? new MonitorDescriptor("", ScreenRect.FromSize(0, 0, 1920, 1080), ScreenRect.FromSize(0, 0, 1920, 1040), 1, true);
    }

    private List<RectD> OthersOn(MonitorDescriptor monitor, DesktopWidgetWindow except)
    {
        var result = new List<RectD>();
        foreach (var other in _windows.Values)
        {
            if (ReferenceEquals(other, except) || other.Bounds is not { } b || MonitorAt(b).DeviceName != monitor.DeviceName)
            {
                continue;
            }

            result.Add(new RectD((b.Left - monitor.Bounds.Left) / monitor.Scale, (b.Top - monitor.Bounds.Top) / monitor.Scale, other.WidthDip, other.HeightDip));
        }

        return result;
    }

    private void ShowOverlay(MonitorDescriptor monitor)
    {
        if (!W.ShowGridWhileMoving)
        {
            return;
        }

        _overlay ??= new GridOverlayWindow(_pinner);
        _overlay.ShowFor(monitor, Grid);
        foreach (var w in _windows.Values)
        {
            _pinner.BringToFrontOfPinned(new WindowInteropHelper(w).Handle); // widgets stay above the dots
        }
    }

    /// <summary>Brings windows in line with the settings (enable/disable, lock, layout mode, positions).</summary>
    private void Sync()
    {
        foreach (var id in WidgetIds.DefaultOrder)
        {
            var wanted = Enabled(W, id);
            var exists = _windows.TryGetValue(id, out var window);
            if (wanted && !exists)
            {
                Open(id);
            }
            else if (!wanted && exists)
            {
                CloseWidget(window!);
            }
        }

        foreach (var window in _windows.Values)
        {
            window.UpdateHints();
        }

        PlaceAll();
        ScheduleRecheck();
    }

    private void Open(string id)
    {
        (FrameworkElement view, WidgetViewModel vm) = id switch
        {
            WidgetIds.Timer => ((FrameworkElement)new TimerWidget(), (WidgetViewModel)new FocusTimerViewModel(_settings)),
            WidgetIds.Weather => (new WeatherWidget(), new WeatherViewModel(_settings, _http)),
            WidgetIds.Calendar => (new CalendarWidget(), new CalendarViewModel(_settings, _http)),
            WidgetIds.Twitch => (new TwitchWidget(), new TwitchChatViewModel(_settings)),
            _ => (new StatsWidget(), new StatsViewModel(_sensors)),
        };

        var window = new DesktopWidgetWindow(id, view, vm, _pinner, this);
        _windows[id] = window;
        window.Show();
    }

    private void CloseWidget(DesktopWidgetWindow window)
    {
        _windows.Remove(window.Id);
        window.Close();
        window.ViewModel.Dispose();
    }

    /// <summary>
    /// Saved placement (in grid mode snapped and de-overlapped) or the default column at the right edge of
    /// the primary monitor. Defaults are not saved, so "reset positions" brings them back.
    /// </summary>
    private void PlaceAll()
    {
        var monitors = MonitorService.GetMonitors();
        var primary = NotchLayout.SelectMonitor(monitors, null);
        if (primary is null)
        {
            return;
        }

        var placed = new Dictionary<string, List<RectD>>();
        var stackY = DefaultTop;
        foreach (var id in WidgetIds.DefaultOrder)
        {
            if (!_windows.TryGetValue(id, out var window) || window.IsInteracting)
            {
                continue;
            }

            MonitorDescriptor monitor;
            RectD rect;
            if (W.Positions.TryGetValue(id, out var saved) &&
                monitors.FirstOrDefault(m => string.Equals(m.DeviceName, saved.Monitor, StringComparison.OrdinalIgnoreCase)) is { } savedMonitor)
            {
                monitor = savedMonitor;
                var (w, h) = ClampSize(window, saved.Width ?? DesktopWidgetWindow.NaturalWidth, saved.Height ?? window.NaturalHeight);
                rect = new RectD(saved.X, saved.Y, w, h);
            }
            else
            {
                monitor = primary;
                var x = (primary.Bounds.Width / primary.Scale) - DesktopWidgetWindow.NaturalWidth - DefaultRightMargin;
                rect = new RectD(x, stackY, DesktopWidgetWindow.NaturalWidth, window.NaturalHeight);
                stackY += window.NaturalHeight + StackGap;
            }

            var work = WorkAreaDip(monitor);
            if (IsGrid)
            {
                rect = rect with
                {
                    Width = WidgetGrid.SnapLength(rect.Width, Grid.Cell, MinWidth),
                    Height = WidgetGrid.SnapLength(rect.Height, Grid.Cell, window.NaturalHeight),
                };
                var others = placed.TryGetValue(monitor.DeviceName, out var list) ? list : (placed[monitor.DeviceName] = []);
                rect = WidgetGrid.FindFreeSpot(rect, others, Grid, work, GridGap);
                others.Add(rect);
            }
            else
            {
                rect = KeepInside(rect, work);
            }

            window.Place(ToPlacement(monitor, rect));
        }
    }

    private void ScheduleRecheck()
    {
        _recheck.Stop();
        _recheck.Start();
    }

    private void RecheckAll()
    {
        foreach (var w in _windows.Values)
        {
            w.CheckOnScreen(_sessionLocked);
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        _sessionLocked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect;
        Application.Current?.Dispatcher.BeginInvoke(RecheckAll);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var w in _windows.Values)
            {
                switch (w.ViewModel)
                {
                    case WeatherViewModel weather:
                        _ = weather.RefreshNowAsync();
                        break;
                    case CalendarViewModel calendar:
                        _ = calendar.RefreshNowAsync();
                        break;
                }
            }

            ScheduleRecheck();
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            PlaceAll();
            ScheduleRecheck();
        });
}
