using System.Windows;
using System.Windows.Threading;
using System.Net.Http;
using NotchPC.App.Apps;
using NotchPC.App.Clipboard;
using NotchPC.App.Games;
using NotchPC.App.Infrastructure;
using NotchPC.App.Music;
using NotchPC.App.Notifications;
using NotchPC.App.QuickSettings;
using NotchPC.App.Remote;
using NotchPC.App.Notch;
using NotchPC.App.Settings;
using NotchPC.App.Shelf;
using NotchPC.App.Tray;
using NotchPC.App.Widgets;
using NotchPC.Core.Localization;
using NotchPC.Core.Lyrics;
using NotchPC.Core.Settings;
using NotchPC.Platform.Media;
using NotchPC.Platform.Startup;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NotchPC.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in OnExit; Application has no Dispose lifecycle.")]
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private SettingsService? _settings;
    private NotchWindow? _notch;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private MusicViewModel? _music;
    private HttpClient? _http;
    private SensorsClient? _sensors;
    private GameOverlayController? _overlay;
    private GameLibraryService? _games;
    private GameSessionTracker? _sessions;
    private RemoteServer? _remote;
    private NotchPC.App.Audio.AudioMixerViewModel? _mixer;
    private NotchPC.App.Web.WebViewModel? _web;
    private TaskbarPill? _taskbarPill;
    private NotchPC.App.Updates.UpdateService? _updates;
    private WidgetManager? _widgets;
    private ClipboardService? _clipboard;
    private NotchPC.App.Shelf.ShelfViewModel? _shelf;
    private CaptureService? _capture;
    private QuickSettingsViewModel? _quick;
    private NotificationHub? _hub;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = SingleInstance.TryAcquire();
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("Unhandled exception", args.ExceptionObject as Exception);
        AppLog.Info($"Start {typeof(App).Assembly.GetName().Version}");

        _settings = new SettingsService(new SettingsStore(System.IO.Path.Combine(SettingsStore.DefaultDirectory, "settings.json")));
        Loc.SetCulture(Loc.Resolve(_settings.Current.Language));
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);
        ThemeService.Apply(_settings.Current.Theme);
        _settings.Changed += (_, _) => ThemeService.Apply(_settings.Current.Theme);

        // The installer passes the user's choice once: "--autostart on" / "--autostart off".
        var autostartArg = Array.IndexOf(e.Args, "--autostart");
        if (autostartArg >= 0 && autostartArg + 1 < e.Args.Length)
        {
            var on = string.Equals(e.Args[autostartArg + 1], "on", StringComparison.OrdinalIgnoreCase);
            _settings.Update(s => s.StartWithWindows = on);
        }

        SyncAutostart(_settings.Current.StartWithWindows);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(LrclibClient.UserAgent);
        var lyricsCache = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotchPC", "lyrics-cache");
        var media = new MediaSessionService { Trace = (msg, ex) => AppLog.Error(msg, ex) };
        _music = new MusicViewModel(_settings, media, new LrclibClient(_http, lyricsCache));

        _clipboard = new ClipboardService(_settings, System.IO.Path.Combine(SettingsStore.DefaultDirectory, "clipboard"));
        _notch = new NotchWindow(_settings, _music, new ClipboardViewModel(_clipboard), _shelf = new ShelfViewModel(_settings), _capture = new CaptureService(_settings), _quick = new QuickSettingsViewModel());
        _notch.SettingsRequested += (_, _) => ShowSettings();
        _hub = new NotificationHub(_settings);
        _notch.AttachNotifications(_hub, new NotificationsViewModel(_hub));
        _notch.Show();
        _ = _music.StartAsync();

        System.Windows.Forms.Application.EnableVisualStyles();
        _tray = new TrayIcon(() => _notch.IsUserHidden);
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.ToggleNotchRequested += (_, _) => _notch.IsUserHidden = !_notch.IsUserHidden;
        _tray.ExitRequested += (_, _) =>
        {
            _updates?.InstallOnExit(); // a downloaded update installs when NotchPC is closed (like SpotiTube)
            Shutdown();
        };
        Loc.CultureChanged += (_, _) => _tray.RefreshTexts();

        _sensors = new SensorsClient();
        AppsViewModel.EnsureDefaults(_settings); // the phone lists the shortcuts even without the widget
        StickyNotesViewModel.ImportOldNote(_settings); // the removed quick-note tab: its text becomes a sticky note
        _games = new GameLibraryService();
        _ = _games.GetAsync(); // names for play sessions
        var gameHistory = new GameHistoryStore();
        _widgets = new WidgetManager(_settings, _http, _sensors, _games, gameHistory);
        _widgets.Start();
        _updates = new NotchPC.App.Updates.UpdateService(_settings, _hub);
        _updates.Start();
        _sessions = new GameSessionTracker(gameHistory, exe => _games.ForExe(exe));
        _sessions.Ended += (_, s) => _notch.ShowFlash("\uE7FC", GameSessionText.Flash(s));
        _overlay = new GameOverlayController(_settings, _sensors, _notch.Fullscreen, _sessions);
        _remote = new RemoteServer(_settings, _music, _overlay, _hub, _shelf, _clipboard);
        _notch.AttachPhone(new PhoneViewModel(_settings, _remote));
        _notch.AttachAudio(_mixer = new NotchPC.App.Audio.AudioMixerViewModel());
        _notch.AttachWeb(_web = new NotchPC.App.Web.WebViewModel(_settings));
        _notch.AttachSearch(new NotchPC.App.Search.SearchViewModel(_http, () => _notch.CollapseNow()));
        _taskbarPill = new TaskbarPill(_settings, _music, _notch.Fullscreen, () => _notch.OpenTabFromOutside(NotchPC.Core.Settings.TabIds.Music));

        _singleInstance.OnActivationRequested(() => Dispatcher.BeginInvoke(ShowSettings));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings?.Flush();
        _tray?.Dispose();
        if (_settingsWindow is not null)
        {
            _settingsWindow.AllowClose = true;
            _settingsWindow.Close();
        }

        _notch?.Close();
        _clipboard?.Dispose();
        _quick?.Dispose();
        _hub?.Dispose();
        _widgets?.Dispose();
        _remote?.Dispose();
        _mixer?.Dispose();
        _web?.Dispose();
        _taskbarPill?.Dispose();
        _updates?.Dispose();
        _overlay?.Dispose();
        _sessions?.Dispose();
        _sensors?.Dispose();
        _music?.Dispose();
        _http?.Dispose();
        _singleInstance?.Dispose();
        AppLog.Info("Exit");
        base.OnExit(e);
    }

    private static void SyncAutostart(bool wanted)
    {
        try
        {
            // Rewrites the entry when the exe was moved; removes a stale one when disabled.
            if (wanted != AutostartService.IsEnabled() || wanted)
            {
                AutostartService.Set(wanted);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Error("Autostart registry update failed", ex);
        }
        catch (System.Security.SecurityException ex)
        {
            AppLog.Error("Autostart registry update failed", ex);
        }
    }

    /// <summary>Starts a fresh NotchPC and closes this one (needed after registering the package identity).</summary>
    public void Restart()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return;
        }

        _settings?.Flush();
        _singleInstance?.Dispose(); // let the new process take the single-instance lock
        _singleInstance = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
        Shutdown();
    }

    private void ShowSettings()
    {
        if (_settings is null)
        {
            return;
        }

        _settingsWindow ??= new SettingsWindow(new SettingsViewModel(_settings, _http!, _sensors!, _clipboard!, _capture!, _hub!, _overlay!, _remote!, _updates!));
        _settingsWindow.ShowAndActivate();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);
        // Keep the overlay alive: a broken tab must not kill the whole app.
        e.Handled = true;
    }
}
