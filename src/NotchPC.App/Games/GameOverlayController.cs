using System.Windows.Interop;
using System.Windows.Threading;
using NotchPC.App.Infrastructure;
using NotchPC.App.Widgets;
using NotchPC.Core.Games;
using NotchPC.Core.Localization;
using NotchPC.Core.Widgets;
using NotchPC.Platform.Games;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Games;

/// <summary>
/// Shows the game overlay while a game is in front. Follows the foreground window through the notch's
/// fullscreen watcher (event driven, no polling while nothing plays). While the overlay is visible: one tick per
/// second, the sensors helper and the FPS meter run; when it hides, all three stop.
/// </summary>
internal sealed class GameOverlayController : IDisposable
{
    private const int HotkeyId = 0x4E50; // "NP"
    private static readonly TimeSpan MessageTime = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan PreviewTime = TimeSpan.FromSeconds(8);

    /// <summary>
    /// After the game leaves the front the meters keep running this long: alt-tabbing out and back does not restart
    /// the FPS session and the sensors helper every time (each restart costs a short hitch).
    /// </summary>
    private static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(20);

    private readonly SettingsService _settings;
    private readonly SensorsClient _sensors;
    private readonly FullscreenWatcher _watcher;
    private readonly GameSessionTracker _sessions;
    private readonly FpsMeter _fps = new();
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _messageTimer;
    private readonly DispatcherTimer _stopLater;
    private readonly HwndSource _messages;
    private GameOverlayWindow? _window;
    private ForegroundApp? _app;
    private SensorSnapshot? _snapshot;
    private string? _registeredHotkey;
    private bool _visible;
    private bool _preview;
    private bool _fpsTried;
    private bool _measuring;
    private readonly HashSet<object> _remote = [];
    private IReadOnlyDictionary<uint, double> _rates = new Dictionary<uint, double>();

    public GameOverlayController(SettingsService settings, SensorsClient sensors, FullscreenWatcher watcher, GameSessionTracker sessions)
    {
        _settings = settings;
        _sensors = sensors;
        _watcher = watcher;
        _sessions = sessions;
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Update();
        _stopLater = new DispatcherTimer(DispatcherPriority.Background) { Interval = StopDelay };
        _stopLater.Tick += (_, _) =>
        {
            _stopLater.Stop();
            if (!_visible && _remote.Count == 0)
            {
                StopMeasuring();
            }
        };
        _messageTimer = new DispatcherTimer { Interval = MessageTime };
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            _preview = false;
            Evaluate(force: true);
        };

        // Message-only window for the global shortcut (WM_HOTKEY).
        _messages = new HwndSource(new HwndSourceParameters("NotchPC.GameOverlay") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _messages.AddHook(OnMessage);

        _sensors.SnapshotReceived += OnSnapshot;
        _watcher.Evaluated += OnForegroundChanged;
        _settings.Changed += OnSettingsChanged;
        ApplyHotkey();
        Evaluate();
    }

    /// <summary>Settings → status of the shortcut (false: another app owns that combination).</summary>
    public bool HotkeyWorks { get; private set; } = true;

    /// <summary>The overlay window itself shows (a game can also be followed just for the session stats).</summary>
    private bool ShowsWindow => _settings.Current.GameOverlay.Enabled || _preview;

    /// <summary>Plain-language state of the FPS counter, null before the overlay first showed.</summary>
    public string? FpsStatus => !_fpsTried
        ? null
        : _fps.LastError is { } e
            ? Loc.Format("Overlay.Fps.Error", e)
            : Loc.Get("Overlay.Fps.Ok");

    public event EventHandler? StatusChanged;

    /// <summary>Once a second while a phone shows the game stats.</summary>
    public event EventHandler<GameStats>? StatsUpdated;

    /// <summary>A phone shows the game stats: sensors and FPS run even while the overlay itself is hidden.</summary>
    public void AcquireStats(object consumer)
    {
        if (_remote.Add(consumer) && _remote.Count == 1)
        {
            StartMeasuring();
        }
    }

    public void ReleaseStats(object consumer)
    {
        if (_remote.Remove(consumer) && _remote.Count == 0 && !_visible)
        {
            StopMeasuring();
        }
    }

    /// <summary>Same as the shortcut (also from the phone).</summary>
    public void ToggleForForeground() => OnHotkey();

    /// <summary>Shows the overlay for a few seconds on the monitor of the window in front (settings button).</summary>
    public void Preview()
    {
        // From NotchPC's own settings window there is no foreground app: use the primary monitor.
        var app = ForegroundApp.Current()
                  ?? (PrimaryMonitor() is { } m ? new ForegroundApp(IntPtr.Zero, (uint)Environment.ProcessId, "NotchPC", m, false) : null);
        if (app is null)
        {
            return;
        }

        _preview = true;
        Show(app);
        _messageTimer.Interval = PreviewTime;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    public void Dispose()
    {
        _watcher.Evaluated -= OnForegroundChanged;
        _settings.Changed -= OnSettingsChanged;
        _sensors.SnapshotReceived -= OnSnapshot;
        if (_registeredHotkey is not null)
        {
            GlobalHotkey.Unregister(_messages.Handle, HotkeyId);
        }

        Hide();
        _stopLater.Stop();
        StopMeasuring();
        _fps.Dispose();
        _window?.Close();
        _messages.Dispose();
    }

    private static Core.Layout.MonitorDescriptor? PrimaryMonitor() =>
        Platform.Monitors.MonitorService.GetMonitors().FirstOrDefault(m => m.IsPrimary);

    private void OnSnapshot(object? sender, SensorSnapshot snapshot) => _snapshot = snapshot;

    private void OnForegroundChanged(object? sender, EventArgs e) => Evaluate();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ApplyHotkey();
        _window?.SetClockVisible(_settings.Current.GameOverlay.ShowClock);
        if (_visible && _app is not null && _window is not null)
        {
            _window.Place(_app.Monitor, _settings.Current.GameOverlay.Position);
        }

        Evaluate();
    }

    private void Evaluate(bool force = false)
    {
        if ((_preview || _messageTimer.IsEnabled) && !force)
        {
            return; // a preview or a short message is on screen
        }

        var s = _settings.Current.GameOverlay;
        if (!s.Enabled && !s.SessionStats)
        {
            Hide();
            return;
        }

        var app = ForegroundApp.Current();
        if (app is null)
        {
            // NotchPC itself in front (its notch or settings): keep whatever shows; desktop / taskbar: hide.
            if (WindowHelper.Foreground() is var fg && fg != IntPtr.Zero && WindowHelper.ProcessIdOf(fg) == (uint)Environment.ProcessId)
            {
                return;
            }

            Hide();
            return;
        }

        if (GameDetection.IsGame(app.ProcessName, app.CoversMonitor, s.AutoFullscreen, s.Games, s.Ignored))
        {
            Show(app);
        }
        else
        {
            Hide();
        }
    }

    private void Show(ForegroundApp app)
    {
        var changed = _app?.ProcessId != app.ProcessId;
        _app = app;
        if (!_preview && _settings.Current.GameOverlay.SessionStats)
        {
            _sessions.InFront(app.ProcessId, app.ProcessName);
        }

        _window ??= new GameOverlayWindow();
        _window.SetClockVisible(_settings.Current.GameOverlay.ShowClock);
        if (!_visible)
        {
            _stopLater.Stop();
            StartMeasuring(); // still running when the game was in front a moment ago
            _fps.TakeRates(); // count from now, not the time spent elsewhere

            _visible = true;
            AppLog.Info($"Game overlay on for {app.ProcessName}");
        }
        else if (changed)
        {
            _fps.TakeRates(); // start counting the new game from now
        }

        Update();
        if (ShowsWindow)
        {
            _window.Show();
            _window.Place(app.Monitor, _settings.Current.GameOverlay.Position);
        }
        else
        {
            _window.Hide();
        }
    }

    private void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        _app = null;
        _window?.Hide();
        _sessions.Away();
        if (_remote.Count == 0)
        {
            _stopLater.Stop();
            _stopLater.Start();
        }

        AppLog.Info("Game overlay off");
    }

    /// <summary>Sensors helper + FPS session + the 1 s tick: only while the overlay or a phone needs them.</summary>
    private void StartMeasuring()
    {
        if (_measuring)
        {
            return;
        }

        _measuring = true;
        _sensors.Acquire(this);
        if (!_fps.Start())
        {
            AppLog.Info($"FPS meter could not start (error {_fps.LastError})");
        }

        _fpsTried = true;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        _tick.Start();
    }

    private void StopMeasuring()
    {
        if (!_measuring)
        {
            return;
        }

        _measuring = false;
        _tick.Stop();
        _sensors.Release(this);
        _fps.Stop();
    }

    private void Update()
    {
        if (_fps.IsRunning)
        {
            _rates = _fps.TakeRates();
        }

        if (_remote.Count > 0)
        {
            var front = ForegroundApp.Current();
            double? frontFps = !_fps.IsRunning ? null : front is not null && _rates.TryGetValue(front.ProcessId, out var r) ? r : 0;
            StatsUpdated?.Invoke(this, new GameStats(front?.ProcessName, frontFps, _snapshot, _visible && front is not null && _app?.ProcessId == front.ProcessId));
        }

        if (_window is null || _app is null || !_visible)
        {
            return;
        }

        double? fps = null;
        if (_fps.IsRunning && _rates.TryGetValue(_app.ProcessId, out var rate))
        {
            fps = rate;
        }

        if (!_preview)
        {
            _sessions.Sample(_fps.IsRunning ? fps ?? 0 : null);
        }

        if (!ShowsWindow)
        {
            return;
        }

        var s = _snapshot;
        _window.ShowStats(
            _fps.IsRunning ? fps ?? 0 : null,
            s is null ? "—" : OverlayFormat.LoadAndTemp(s.CpuLoad, s.CpuTemp),
            s is null ? "—" : OverlayFormat.LoadAndTemp(s.GpuLoad, s.GpuTemp),
            s is null ? "—" : OverlayFormat.LoadAndTemp(s.RamLoad, null),
            DateTime.Now.ToString("t", Loc.Culture));
        _window.KeepOnTop();
    }

    /// <summary>The shortcut: overlay off for this app if it shows, on (and remembered as a game) if it does not.</summary>
    private void OnHotkey()
    {
        if (ForegroundApp.Current() is not { } app)
        {
            return;
        }

        var shown = _visible && _app?.ProcessId == app.ProcessId && !_preview;
        var s = _settings.Current.GameOverlay;
        var (games, ignored) = GameDetection.Toggle(app.ProcessName, shown, s.Games, s.Ignored);
        _settings.Update(x =>
        {
            x.GameOverlay.Enabled = true;
            x.GameOverlay.Games = games;
            x.GameOverlay.Ignored = ignored;
        });
        AppLog.Info($"Game overlay shortcut: {(shown ? "off" : "on")} for {app.ProcessName}");

        if (shown)
        {
            // Say so briefly, then hide (the app stays in the ignored list until the shortcut is pressed again).
            Show(app);
            _window!.ShowMessage(Loc.Format("Overlay.HiddenFor", app.ProcessName, OverlayHotkeys.Display(s.Hotkey)));
            _messageTimer.Interval = MessageTime;
            _messageTimer.Stop();
            _messageTimer.Start();
        }
        else
        {
            _preview = false;
            _messageTimer.Stop();
            Evaluate(force: true);
        }
    }

    private void ApplyHotkey()
    {
        var wanted = _settings.Current.GameOverlay.Enabled ? _settings.Current.GameOverlay.Hotkey : OverlayHotkeys.None;
        if (wanted == _registeredHotkey)
        {
            return;
        }

        if (_registeredHotkey is not null)
        {
            GlobalHotkey.Unregister(_messages.Handle, HotkeyId);
            _registeredHotkey = null;
        }

        HotkeyWorks = true;
        if (OverlayHotkeys.Keys(wanted) is { } keys)
        {
            HotkeyWorks = GlobalHotkey.Register(_messages.Handle, HotkeyId, keys.Modifiers, keys.Key);
            if (HotkeyWorks)
            {
                _registeredHotkey = wanted;
            }
            else
            {
                AppLog.Info($"Overlay shortcut {wanted} is taken by another app");
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == GlobalHotkey.WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            OnHotkey();
        }

        return IntPtr.Zero;
    }
}

/// <summary>What the phone's game tab shows: the app in front, its FPS, the sensors, whether the PC overlay is on.</summary>
internal sealed record GameStats(string? App, double? Fps, SensorSnapshot? Sensors, bool OverlayOn);
