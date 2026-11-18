using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NotchPC.App.Apps;
using NotchPC.App.Clipboard;
using NotchPC.App.Infrastructure;
using NotchPC.App.Music;
using NotchPC.App.Notifications;
using NotchPC.App.QuickSettings;
using NotchPC.App.Shelf;
using NotchPC.App.Widgets;
using NotchPC.Core.Layout;
using NotchPC.Core.Localization;
using NotchPC.Core.Settings;
using NotchPC.Platform.Monitors;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Notch;

/// <summary>
/// The island at the top of the screen. The window itself has a fixed size (big enough for the expanded panel
/// plus spring overshoot) and is fully transparent, so everything except the island is click-through.
/// Only the island's size and corner radius are animated; the HWND never resizes during animation.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in OnClosed (WPF window lifecycle).")]
public partial class NotchWindow : Window
{
    public static readonly DependencyProperty IslandRadiusProperty = DependencyProperty.Register(
        nameof(IslandRadius), typeof(double), typeof(NotchWindow),
        new PropertyMetadata(CollapsedRadius, (d, e) => ((NotchWindow)d).Island.CornerRadius = new CornerRadius((double)e.NewValue)));

    public static readonly DependencyProperty SelectedTabProperty = DependencyProperty.Register(
        nameof(SelectedTab), typeof(NotchTab), typeof(NotchWindow), new PropertyMetadata(null));

    // Sizes in DIPs.
    private const double CollapsedWidth = 180;
    private const double MiniPlayerWidth = 330;
    private const double FlashWidth = 290;
    private const double BannerWidth = 400;
    private const double BannerHeight = 74;
    private const double BannerRadius = 26;
    private const double CollapsedHeight = 32;
    private const double CollapsedRadius = 16;
    private const double ExpandedWidth = 470;
    private const double ExpandedHeight = 190;
    private const double ExpandedRadius = 28;
    // The window is resized between two fixed "canvases" (never during an animation). WPF redraws and copies the
    // whole transparent window on every animation frame, so the small collapsed canvas keeps the pill's equalizer cheap.
    private const double CanvasWidth = ExpandedWidth + 90;   // room for spring overshoot
    private const double MaxExpandedHeight = 380; // tallest tab panel (clipboard)
    private const double CanvasHeight = 64 + MaxExpandedHeight + 24; // 64 = max top offset
    private const double CollapsedCanvasWidth = MiniPlayerWidth + 64;
    private const double CollapsedCanvasExtraHeight = CollapsedHeight + 14;
    private const double LyricsGap = 12;
    private const int WmDisplayChange = 0x007E;

    private static readonly Duration ExpandDuration = TimeSpan.FromMilliseconds(460);
    private static readonly Duration CollapseDuration = TimeSpan.FromMilliseconds(300);
    private static readonly double[] EqualizerPeriods = [0.42, 0.58, 0.36, 0.5];

    private readonly SettingsService _settings;
    private readonly MusicViewModel _music;
    private readonly ClipboardViewModel _clipboard;
    private readonly ShelfViewModel _shelf;
    private readonly AppsViewModel _apps;
    private readonly QuickSettingsViewModel _quick;
    private bool _captureHidden;
    private readonly FullscreenWatcher _fullscreen = new();

    /// <summary>Foreground / fullscreen changes (also used by the game overlay).</summary>
    internal FullscreenWatcher Fullscreen => _fullscreen;
    private readonly ObservableCollection<NotchTab> _tabs = [];
    private readonly DispatcherTimer _expandTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _miniOffTimer;
    private readonly DispatcherTimer _flashTimer;
    private readonly DispatcherTimer _bannerTimer;
    private bool _bannerActive;
    private NotificationsViewModel? _notifications;
    private readonly Brush _defaultBackground;
    private readonly Brush _defaultBorder;
    private LyricsWindow? _lyricsWindow;
    private MonitorDescriptor? _monitor;
    private IntPtr _hwnd;
    private bool _expanded;
    private bool _userHidden;
    private bool _miniActive;
    private bool _equalizerRunning;
    private bool _largeCanvas;
    private bool _keyboard;
    private IntPtr _previousForeground;
    private bool _flashActive;
    private int _animationGeneration;

    internal NotchWindow(SettingsService settings, MusicViewModel music, ClipboardViewModel clipboard, ShelfViewModel shelf, AppsViewModel apps, QuickSettingsViewModel quick)
    {
        _settings = settings;
        _music = music;
        _clipboard = clipboard;
        _shelf = shelf;
        InitializeComponent();

        Width = CollapsedCanvasWidth;
        Height = CollapsedCanvasExtraHeight + _settings.Current.Notch.TopOffset;
        TabBar.ItemsSource = _tabs;
        Island.CornerRadius = new CornerRadius(IslandRadius); // the DP callback only fires on changes
        ExpandedContent.Width = ExpandedWidth - 2;
        MusicPanel.DataContext = music;
        WidgetsPanel.DataContext = new WidgetsTabViewModel(settings);
        ClipboardPanel.DataContext = clipboard;
        ShelfPanel.DataContext = shelf;
        AppsPanel.DataContext = apps;
        QuickSettingsPanel.DataContext = quick;
        _quick = quick;
        _quick.LayoutChanged += (_, _) =>
        {
            if (_expanded && SelectedTab?.Id == TabIds.QuickSettings)
            {
                Select(SelectedTab);
            }
        };
        _apps = apps;
        _apps.RowsChanged += (_, _) =>
        {
            if (_expanded && SelectedTab?.Id == TabIds.Apps)
            {
                Select(SelectedTab); // re-animates to the new height
            }
        };
        _defaultBackground = Island.Background;
        _defaultBorder = Island.BorderBrush;

        // Timers only run for the short moment between a hover change and the reaction.
        _expandTimer = new DispatcherTimer(DispatcherPriority.Input);
        _expandTimer.Tick += (_, _) =>
        {
            _expandTimer.Stop();
            Expand();
        };
        _collapseTimer = new DispatcherTimer(DispatcherPriority.Input);
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_moving)
            {
                Collapse(); // never while the notch is being dragged
            }
        };
        // Players briefly report "paused" between tracks: wait before shrinking the pill.
        _miniOffTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _miniOffTimer.Tick += (_, _) =>
        {
            _miniOffTimer.Stop();
            SetMiniActive(false);
        };

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
        _flashTimer.Tick += (_, _) => EndFlash();
        _bannerTimer = new DispatcherTimer();
        _bannerTimer.Tick += (_, _) => EndBanner();

        MouseEnter += OnPointerEnter;
        MouseLeave += OnPointerLeave;
        PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        Island.PreviewMouseLeftButtonDown += OnMoveStart;
        Island.PreviewMouseMove += OnMoveDrag;
        Island.PreviewMouseLeftButtonUp += OnMoveEnd;
        Island.LostMouseCapture += (_, _) => FinishMove();
        AllowDrop = true;
        DragEnter += OnFileDragEnter;
        DragOver += OnFileDragOver;
        DragLeave += OnFileDragLeave;
        Drop += OnFileDrop;
        Deactivated += (_, _) =>
        {
            // Typing finished elsewhere: close once the mouse is not over the notch.
            if (_keyboard && !IsMouseOver)
            {
                _keyboard = false;
                WindowHelper.MakeOverlay(_hwnd, noActivate: true);
                _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _settings.Current.Notch.CollapseDelayMs));
                _collapseTimer.Start();
            }
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
        IsVisibleChanged += (_, _) => OnOverlayVisibilityChanged();
        _fullscreen.Changed += (_, _) => UpdateVisibility();
        _fullscreen.Trace = AppLog.Info;
        _settings.Changed += OnSettingsChanged;
        _music.PropertyChanged += OnMusicChanged;
        Loc.CultureChanged += OnCultureChanged;

        RebuildTabs();
        ApplyCollapsedImmediately();
    }

    public event EventHandler? SettingsRequested;

    /// <summary>Hidden from the tray menu.</summary>
    public bool IsUserHidden
    {
        get => _userHidden;
        set
        {
            _userHidden = value;
            UpdateVisibility();
        }
    }

    public double IslandRadius
    {
        get => (double)GetValue(IslandRadiusProperty);
        set => SetValue(IslandRadiusProperty, value);
    }

    public NotchTab? SelectedTab
    {
        get => (NotchTab?)GetValue(SelectedTabProperty);
        set => SetValue(SelectedTabProperty, value);
    }

    private double CurrentCollapsedWidth => _flashActive ? FlashWidth : _miniActive ? MiniPlayerWidth : CollapsedWidth;

    /// <summary>Height of the expanded panel for the selected tab (lists need more room than the player).</summary>
    private double CurrentExpandedHeight => SelectedTab?.Id switch
    {
        TabIds.Clipboard or TabIds.Notifications or TabIds.Audio => MaxExpandedHeight,
        TabIds.Files => 260,
        TabIds.Phone => 270,
        TabIds.Web => 74 + (84 * _webRows),
        TabIds.Apps => 106 + (74 * _apps.Rows),
        TabIds.QuickSettings => 142 + (76 * _quick.TileRows),
        _ => ExpandedHeight,
    };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowHelper.MakeOverlay(_hwnd, noActivate: true);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _fullscreen.Attach(_hwnd);
        Reposition();
    }

    protected override void OnClosed(EventArgs e)
    {
        _settings.Changed -= OnSettingsChanged;
        _music.PropertyChanged -= OnMusicChanged;
        Loc.CultureChanged -= OnCultureChanged;
        _fullscreen.Dispose();
        _lyricsWindow?.Close();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_fullscreen.TryHandleMessage(msg, wParam, lParam))
        {
            handled = true;
        }
        else if (msg == WmDisplayChange)
        {
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    /// <summary>Centres the window at the top of the configured monitor (physical pixels, per-monitor DPI).</summary>
    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var monitor = _moving ? _moveMonitor : NotchLayout.SelectMonitor(MonitorService.GetMonitors(), _settings.Current.Notch.Monitor);
        if (monitor is null)
        {
            return;
        }

        _monitor = monitor;
        var offset = NotchOffset;
        var rect = _largeCanvas
            ? NotchLayout.TopCenter(monitor, CanvasWidth, CanvasHeight, offset)
            : NotchLayout.TopCenter(monitor, CollapsedCanvasWidth, _settings.Current.Notch.TopOffset + CollapsedCanvasExtraHeight, offset);
        if (WindowHelper.GetBounds(_hwnd) != rect)
        {
            WindowHelper.PlaceTopmost(_hwnd, rect);
        }

        _fullscreen.SetTargetMonitor(monitor.Bounds);
        PlaceLyricsWindow();
    }

    private double LyricsTop => _settings.Current.Notch.TopOffset + ExpandedHeight + LyricsGap;

    /// <summary>The notch's distance from the monitor's centre (DIPs): live while dragging, else the saved one.</summary>
    private double NotchOffset => _moving
        ? _moveOffset
        : _monitor is null ? 0 : NotchLayout.ClampOffset(_monitor, _settings.Current.Notch.HorizontalOffset, CanvasWidth);

    // ---------------------------------------------------------------- moving the notch

    private (int X, int Y)? _moveStart;
    private double _moveGrab;
    private bool _moving;
    private double _moveOffset;
    private MonitorDescriptor? _moveMonitor;

    /// <summary>
    /// The notch is dragged by its tab bar (open) or by the pill (closed). A short press stays a click; only a
    /// sideways move of a few pixels turns into moving the notch.
    /// </summary>
    private void OnMoveStart(object sender, MouseButtonEventArgs e)
    {
        _moveStart = null;
        if (_monitor is null || e.OriginalSource is not DependencyObject source || IsInside<System.Windows.Controls.Primitives.TextBoxBase>(source))
        {
            return;
        }

        // Only the grip moves the notch: clicks anywhere else keep working as before.
        if (!_expanded || !IsInside(source, MoveGrip))
        {
            return;
        }

        e.Handled = true; // the press belongs to the grip

        var cursor = WindowHelper.CursorPosition();
        _moveStart = cursor;
        _moveGrab = (cursor.X - NotchLayout.CenterX(_monitor, NotchOffset)) / _monitor.Scale;
    }

    private void OnMoveDrag(object sender, MouseEventArgs e)
    {
        if (_moveStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var cursor = WindowHelper.CursorPosition();
        if (!_moving)
        {
            if (Math.Abs(cursor.X - start.X) + Math.Abs(cursor.Y - start.Y) < 3)
            {
                return;
            }

            _moving = true;
            _moveMonitor = _monitor;
            _moveOffset = NotchOffset;
            _expandTimer.Stop();
            _collapseTimer.Stop();
            Island.CaptureMouse(); // the tab button under the cursor will not get a click
            Island.Cursor = Cursors.SizeWE;
        }

        // The notch follows the cursor, also onto another monitor.
        var monitors = MonitorService.GetMonitors();
        var under = monitors.FirstOrDefault(m => cursor.X >= m.Bounds.Left && cursor.X < m.Bounds.Right && cursor.Y >= m.Bounds.Top && cursor.Y < m.Bounds.Bottom)
                    ?? _moveMonitor;
        if (under is null)
        {
            return;
        }

        _moveMonitor = under;
        var centre = under.Bounds.Left + (under.Bounds.Width / 2);
        _moveOffset = NotchLayout.ClampOffset(under, ((cursor.X - centre) / under.Scale) - _moveGrab, CanvasWidth);
        Reposition();
        e.Handled = true;
    }

    private void OnMoveEnd(object sender, MouseButtonEventArgs e)
    {
        if (_moving)
        {
            e.Handled = true; // a drag is not a click
            Island.ReleaseMouseCapture();
        }

        _moveStart = null;
    }

    /// <summary>Saves where the notch was dropped (snaps to the centre when dropped close to it).</summary>
    private void FinishMove()
    {
        if (!_moving)
        {
            return;
        }

        _moving = false;
        _moveStart = null;
        Island.Cursor = null;
        var offset = Math.Abs(_moveOffset) < 12 ? 0 : Math.Round(_moveOffset);
        var device = _moveMonitor?.DeviceName ?? _settings.Current.Notch.Monitor;
        _settings.Update(s =>
        {
            s.Notch.HorizontalOffset = offset;
            s.Notch.Monitor = device;
        });
        AppLog.Info($"Notch moved: offset {offset} DIP");
        if (!IsMouseOver)
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _settings.Current.Notch.CollapseDelayMs));
            _collapseTimer.Start();
        }
    }

    private static bool IsInside<T>(DependencyObject node)
        where T : DependencyObject
    {
        for (DependencyObject? n = node; n is not null; n = System.Windows.Media.VisualTreeHelper.GetParent(n) ?? LogicalTreeHelper.GetParent(n))
        {
            if (n is T)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? n = node; n is not null; n = System.Windows.Media.VisualTreeHelper.GetParent(n) ?? LogicalTreeHelper.GetParent(n))
        {
            if (ReferenceEquals(n, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateVisibility()
    {
        var hiddenByFullscreen = _settings.Current.Notch.HideOnFullscreen && _fullscreen.IsFullscreen;
        var shouldShow = !_userHidden && !hiddenByFullscreen && !_captureHidden;

        if (shouldShow && !IsVisible)
        {
            Show();
            WindowHelper.BringToTopmost(_hwnd);
        }
        else if (!shouldShow && IsVisible)
        {
            ApplyCollapsedImmediately();
            Hide();
        }
    }

    private void OnOverlayVisibilityChanged()
    {
        UpdateLyricsWindow();
        UpdateEqualizer();
        UpdatePanelVisibility();
    }

    // ---------------------------------------------------------------- hover / expand / collapse

    private void OnPointerEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_expanded)
        {
            return;
        }

        var delay = _settings.Current.Notch.HoverDelayMs;
        if (delay <= 0)
        {
            Expand();
            return;
        }

        _expandTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _expandTimer.Start();
    }

    private void OnPointerLeave(object sender, MouseEventArgs e)
    {
        if (_moving || _moveStart is not null)
        {
            return; // the window moves under the pointer while it is dragged
        }

        _expandTimer.Stop();
        if (!_expanded || _keyboard)
        {
            return;
        }

        var delay = _settings.Current.Notch.CollapseDelayMs;
        if (delay <= 0)
        {
            Collapse();
            return;
        }

        _collapseTimer.Interval = TimeSpan.FromMilliseconds(delay);
        _collapseTimer.Start();
    }

    private void Expand()
    {
        if (_expanded)
        {
            return;
        }

        _expanded = true;
        SetCanvas(large: true);
        WindowHelper.BringToTopmost(_hwnd);

        // Hovering a banner opens the notifications tab; otherwise, while music plays, the player.
        if (_bannerActive)
        {
            StopBanner();
            if (_tabs.FirstOrDefault(t => t.Id == TabIds.Notifications) is { } notificationsTab)
            {
                Select(notificationsTab);
            }
        }
        else if (_miniActive && _tabs.FirstOrDefault(t => t.Id == TabIds.Music) is { } musicTab)
        {
            Select(musicTab);
        }

        SetHotZone(expanded: true);
        ExpandedContent.Visibility = Visibility.Visible;
        FadeMiniPlayer(show: false);
        UpdateEqualizer();
        UpdateIslandBackground();
        UpdatePanelVisibility();

        ExpandedContent.Height = CurrentExpandedHeight - 2;
        AnimateIsland(ExpandedWidth, CurrentExpandedHeight, ExpandedRadius, new SpringEase { Damping = 0.74, Frequency = 12 }, ExpandDuration, null);
        AnimateContentOpacity(1, TimeSpan.FromMilliseconds(110), TimeSpan.FromMilliseconds(220));
    }

    private void Collapse()
    {
        if (!_expanded)
        {
            return;
        }

        _expanded = false;
        ReleaseKeyboard();
        SetHotZone(expanded: false);
        UpdatePanelVisibility();
        AnimateContentOpacity(0, TimeSpan.Zero, TimeSpan.FromMilliseconds(90));
        AnimateIsland(CurrentCollapsedWidth, CollapsedHeight, CollapsedRadius, new SpringEase { Damping = 1, Frequency = 9 }, CollapseDuration, () =>
        {
            if (!_expanded)
            {
                ExpandedContent.Visibility = Visibility.Collapsed;
                UpdateIslandBackground();
                if (!_bannerActive)
                {
                    SetCanvas(large: false);
                }
            }
        });
        FadeMiniPlayer(show: _miniActive, delayMs: 160);
        UpdateEqualizer();
    }

    /// <summary>Snaps to the collapsed state without animation (used when hiding for fullscreen apps).</summary>
    private void ApplyCollapsedImmediately()
    {
        _expandTimer.Stop();
        _collapseTimer.Stop();
        _expanded = false;
        _animationGeneration++;
        StopBanner();

        Island.BeginAnimation(WidthProperty, null);
        Island.BeginAnimation(HeightProperty, null);
        BeginAnimation(IslandRadiusProperty, null);
        ExpandedContent.BeginAnimation(OpacityProperty, null);
        MiniPlayer.BeginAnimation(OpacityProperty, null);

        Island.Width = CurrentCollapsedWidth;
        Island.Height = CollapsedHeight;
        IslandRadius = CollapsedRadius;
        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Collapsed;
        MiniPlayer.Opacity = _miniActive ? 1 : 0;
        MiniPlayer.Visibility = _miniActive ? Visibility.Visible : Visibility.Collapsed;
        ApplyTopOffset();
        SetHotZone(expanded: false);
        SetCanvas(large: false);
        UpdateIslandBackground();
        UpdatePanelVisibility();
        UpdateEqualizer();
    }

    private void AnimateIsland(double width, double height, double radius, IEasingFunction ease, Duration duration, Action? completed)
    {
        var generation = ++_animationGeneration;

        var w = new DoubleAnimation(width, duration) { EasingFunction = ease };
        var h = new DoubleAnimation(height, duration) { EasingFunction = ease };
        var r = new DoubleAnimation(radius, duration) { EasingFunction = ease };

        w.Completed += (_, _) =>
        {
            if (generation != _animationGeneration)
            {
                return; // superseded by a newer animation
            }

            // Bake the final values and drop the animation clocks, so nothing keeps ticking when idle.
            Island.Width = width;
            Island.Height = height;
            IslandRadius = radius;
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            BeginAnimation(IslandRadiusProperty, null);
            completed?.Invoke();
        };

        Island.BeginAnimation(WidthProperty, w, HandoffBehavior.SnapshotAndReplace);
        Island.BeginAnimation(HeightProperty, h, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(IslandRadiusProperty, r, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateContentOpacity(double to, TimeSpan begin, TimeSpan duration)
    {
        var generation = _animationGeneration;
        var a = new DoubleAnimation(to, duration) { BeginTime = begin };
        a.Completed += (_, _) =>
        {
            if (generation != _animationGeneration)
            {
                return;
            }

            ExpandedContent.Opacity = to;
            ExpandedContent.BeginAnimation(OpacityProperty, null);
        };
        ExpandedContent.BeginAnimation(OpacityProperty, a, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// The hot zone reaches the very top edge of the screen, so throwing the mouse up hits the notch
    /// (and moving into the gap above an expanded panel does not count as leaving it).
    /// </summary>
    private void SetHotZone(bool expanded)
    {
        var top = _settings.Current.Notch.TopOffset;
        if (expanded)
        {
            HotZone.Width = ExpandedWidth;
            HotZone.Height = top + 4;
        }
        else
        {
            HotZone.Width = CurrentCollapsedWidth + 40;
            HotZone.Height = top + CollapsedHeight + 10;
        }
    }

    /// <summary>
    /// The window that should receive a paste: the app the user was in before clicking the notch.
    /// (The notch normally never takes focus; it only does while a search box is being typed in.)
    /// </summary>
    public IntPtr PasteTarget => _keyboard ? _previousForeground : WindowHelper.Foreground();

    /// <summary>The monitor the notch is on (screenshots are taken of this one).</summary>
    internal MonitorDescriptor? Monitor => _monitor;

    /// <summary>Hides the notch (and the lyrics card) so a screenshot does not contain it.</summary>
    internal async Task HideForCaptureAsync()
    {
        _captureHidden = true;
        UpdateVisibility();
        await Task.Delay(220); // let the compositor draw a frame without the notch
    }

    internal void EndCapture()
    {
        _captureHidden = false;
        UpdateVisibility();
    }

    /// <summary>Connects the web tab; opening a page collapses the notch (the panel opens under it).</summary>
    internal void AttachWeb(Web.WebViewModel web)
    {
        WebPanel.DataContext = web;
        web.Opening += (_, _) => CollapseNow();
        void Rows()
        {
            _webRows = Math.Clamp((web.Tiles.Count + 5) / 6, 1, 3);
            if (_expanded && SelectedTab?.Id == TabIds.Web)
            {
                Select(SelectedTab);
            }
        }

        web.Tiles.CollectionChanged += (_, _) => Rows();
        Rows();
    }

    private int _webRows = 1;

    /// <summary>Connects the audio tab (output device and per-app mixer).</summary>
    internal void AttachAudio(Audio.AudioMixerViewModel mixer) => AudioPanel.DataContext = mixer;

    /// <summary>Connects the phone tab (remote status and pairing QR code).</summary>
    internal void AttachPhone(Remote.PhoneViewModel phone) => PhonePanel.DataContext = phone;

    /// <summary>Connects the notifications tab and the banners.</summary>
    internal void AttachNotifications(NotificationHub hub, NotificationsViewModel notifications)
    {
        _notifications = notifications;
        NotificationsPanel.DataContext = notifications;
        hub.BannerRequested += (_, n) => ShowBanner(new NotificationItem(n, hub.LogoFor(n), DateTimeOffset.Now));
        hub.TabRequested += (_, id) => OpenTab(id);
        hub.CollapseRequested += (_, _) =>
        {
            if (_expanded)
            {
                CollapseNow();
            }
            else if (_bannerActive)
            {
                StopBanner();
                ShrinkToPill();
            }
        };
        BannerLayer.Cursor = Cursors.Hand;
        BannerLayer.MouseLeftButtonUp += (_, e) =>
        {
            if (_bannerActive && BannerLayer.DataContext is NotificationItem item)
            {
                e.Handled = true;
                hub.Open(item.Notification);
            }
        };
    }

    /// <summary>Opens the notch on a tab (a click on a NotchPC notification, e.g. the timer).</summary>
    /// <summary>
    /// Opens a tab from somewhere else (the taskbar pill): the pointer is not over the notch, so it closes again on
    /// its own after a few seconds unless the pointer moves onto it.
    /// </summary>
    internal void OpenTabFromOutside(string id)
    {
        OpenTab(id);
        if (_expanded && !IsMouseOver)
        {
            _collapseTimer.Interval = TimeSpan.FromSeconds(4);
            _collapseTimer.Start();
        }
    }

    internal void OpenTab(string id)
    {
        if (_tabs.FirstOrDefault(t => t.Id == id) is not { } tab)
        {
            return;
        }

        if (!_expanded)
        {
            Expand();
        }

        Select(tab);
    }

    /// <summary>
    /// A new notification: the pill grows into a banner (icon, app, title, text) for a few seconds.
    /// Hovering it opens the notifications tab. Skipped while the panel is open or the notch is hidden.
    /// </summary>
    private void ShowBanner(NotificationItem item)
    {
        if (_expanded || !IsVisible)
        {
            return;
        }

        if (_flashActive)
        {
            _flashTimer.Stop();
            _flashActive = false;
            FlashLayer.Visibility = Visibility.Collapsed;
        }

        BannerLayer.DataContext = item;
        _bannerActive = true;
        SetCanvas(large: true);
        WindowHelper.BringToTopmost(_hwnd);
        MiniPlayer.BeginAnimation(OpacityProperty, null);
        MiniPlayer.Visibility = Visibility.Collapsed;
        UpdateEqualizer();
        BannerLayer.Visibility = Visibility.Visible;
        BannerLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(140) });
        AnimateIsland(BannerWidth, BannerHeight, BannerRadius, new SpringEase { Damping = 0.78, Frequency = 11 }, ExpandDuration, null);
        StartGlow();
        _bannerTimer.Stop();
        _bannerTimer.Interval = TimeSpan.FromSeconds(_settings.Current.Notifications.BannerSeconds);
        _bannerTimer.Start();
    }

    /// <summary>Banner time is up: shrink back to the pill.</summary>
    private void EndBanner()
    {
        if (!_bannerActive)
        {
            return;
        }

        if (IsMouseOver && !_expanded && IsVisible)
        {
            Expand(); // the pointer rests on the banner: open the notifications tab instead
            return;
        }

        StopBanner();
        ShrinkToPill();
    }

    /// <summary>Back from the banner shape to the collapsed pill.</summary>
    private void ShrinkToPill()
    {
        if (_expanded || !IsVisible)
        {
            return;
        }

        SetHotZone(expanded: false);
        AnimateIsland(CurrentCollapsedWidth, CollapsedHeight, CollapsedRadius, new SpringEase { Damping = 1, Frequency = 9 }, CollapseDuration, () =>
        {
            if (!_expanded && !_bannerActive)
            {
                SetCanvas(large: false);
            }
        });
        FadeMiniPlayer(show: _miniActive, delayMs: 160);
        UpdateEqualizer();
    }

    /// <summary>Hides the banner content without animating the island (the caller decides the next shape).</summary>
    private void StopBanner()
    {
        _bannerTimer.Stop();
        _bannerActive = false;
        BannerLayer.BeginAnimation(OpacityProperty, null);
        BannerLayer.Visibility = Visibility.Collapsed;
        StopGlow();
    }

    /// <summary>
    /// Makes a new banner stand out: a coloured halo around the island and a lit edge that pulse twice, then stay
    /// softly on until the banner ends. Runs only while the banner shows (no cost otherwise).
    /// </summary>
    private void StartGlow()
    {
        var s = _settings.Current.Notifications;
        if (!s.Glow)
        {
            return;
        }

        var color = GlowColorResolver.Resolve(s.GlowColor);
        GlowEffect.Color = color;
        Glow.Visibility = Visibility.Visible;
        var pulse = new DoubleAnimationUsingKeyFrames();
        foreach (var (opacity, ms) in new[] { (0.0, 0), (1.0, 260), (0.35, 900), (0.9, 1450), (0.3, 2200) })
        {
            pulse.KeyFrames.Add(new EasingDoubleKeyFrame(opacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        }

        Glow.BeginAnimation(OpacityProperty, pulse);

        var edge = new SolidColorBrush(Color.FromArgb(0, color.R, color.G, color.B));
        Island.BorderBrush = edge;
        var edgePulse = new ColorAnimationUsingKeyFrames();
        foreach (var (alpha, ms) in new[] { (0, 0), (255, 260), (90, 900), (230, 1450), (110, 2200) })
        {
            edgePulse.KeyFrames.Add(new EasingColorKeyFrame(Color.FromArgb((byte)alpha, color.R, color.G, color.B), KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        }

        edge.BeginAnimation(SolidColorBrush.ColorProperty, edgePulse);
    }

    private void StopGlow()
    {
        if (Glow.Visibility != Visibility.Visible)
        {
            return;
        }

        Island.BorderBrush = _defaultBorder;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) =>
        {
            if (!_bannerActive)
            {
                Glow.BeginAnimation(OpacityProperty, null);
                Glow.Opacity = 0;
                Glow.Visibility = Visibility.Collapsed;
            }
        };
        Glow.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Closes the panel right away (e.g. before pasting into the app below).</summary>
    public void CollapseNow()
    {
        _expandTimer.Stop();
        _collapseTimer.Stop();
        Collapse();
    }

    /// <summary>Gives keyboard focus back to the previous app (after a paste, or when the notch closes).</summary>
    public void ReturnFocus()
    {
        if (_keyboard)
        {
            ReleaseKeyboard();
        }
    }

    /// <summary>
    /// Briefly widens the collapsed pill with an icon and a message ("Screenshot saved"), Dynamic Island style.
    /// </summary>
    public void ShowFlash(string glyph, string text)
    {
        if (_bannerActive)
        {
            EndBanner();
        }

        FlashGlyph.Text = glyph;
        FlashText.Text = text;
        _flashTimer.Stop();
        _flashActive = true;
        if (!_expanded && IsVisible)
        {
            SetHotZone(expanded: false);
            AnimateIsland(CurrentCollapsedWidth, CollapsedHeight, CollapsedRadius, new SpringEase { Damping = 0.8, Frequency = 11 }, ExpandDuration, null);
        }

        MiniPlayer.Visibility = Visibility.Collapsed;
        FlashLayer.Visibility = Visibility.Visible;
        FlashLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(120) });
        _flashTimer.Start();
    }

    private void EndFlash()
    {
        _flashTimer.Stop();
        _flashActive = false;
        FlashLayer.BeginAnimation(OpacityProperty, null);
        FlashLayer.Visibility = Visibility.Collapsed;
        if (!_expanded && IsVisible)
        {
            SetHotZone(expanded: false);
            AnimateIsland(CurrentCollapsedWidth, CollapsedHeight, CollapsedRadius, new SpringEase { Damping = 1, Frequency = 9 }, CollapseDuration, null);
            FadeMiniPlayer(show: _miniActive, delayMs: 120);
        }
    }

    /// <summary>A text box in a panel was clicked: let the notch take keyboard focus until it collapses.</summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d)
        {
            return;
        }

        for (var node = d; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.TextBoxBase box)
            {
                if (!_keyboard)
                {
                    _previousForeground = WindowHelper.Foreground();
                    WindowHelper.MakeOverlay(_hwnd, noActivate: false);
                    _keyboard = true;
                }

                // The click itself does not activate a no-activate window: take the focus explicitly.
                WindowHelper.SetForeground(_hwnd);
                Activate();
                box.Focus();
                return;
            }

        }
    }

    private void ReleaseKeyboard()
    {
        if (!_keyboard)
        {
            return;
        }

        _keyboard = false;
        Keyboard.ClearFocus();
        WindowHelper.MakeOverlay(_hwnd, noActivate: true);
        if (_previousForeground != IntPtr.Zero)
        {
            WindowHelper.SetForeground(_previousForeground);
        }
    }

    /// <summary>Switches the window between the small (collapsed) and large (expanded) canvas; never during an animation.</summary>
    private void SetCanvas(bool large)
    {
        if (_largeCanvas == large && _hwnd != IntPtr.Zero)
        {
            return;
        }

        _largeCanvas = large;
        Width = large ? CanvasWidth : CollapsedCanvasWidth;
        Height = large ? CanvasHeight : _settings.Current.Notch.TopOffset + CollapsedCanvasExtraHeight;
        Reposition();
    }

    private void ApplyTopOffset() => Island.Margin = new Thickness(0, _settings.Current.Notch.TopOffset, 0, 0);

    // ---------------------------------------------------------------- tabs

    private void RebuildTabs()
    {
        var previous = SelectedTab?.Id;
        _tabs.Clear();
        foreach (var t in _settings.Current.Tabs.Where(t => t.Enabled))
        {
            _tabs.Add(new NotchTab(t.Id));
        }

        var selected = _tabs.FirstOrDefault(t => t.Id == previous && t.Id != TabIds.Settings)
                       ?? _tabs.FirstOrDefault(t => t.Id != TabIds.Settings);
        Select(selected);
    }

    private void Select(NotchTab? tab)
    {
        foreach (var t in _tabs)
        {
            t.IsSelected = ReferenceEquals(t, tab);
        }

        SelectedTab = tab;
        if (_expanded)
        {
            ExpandedContent.Height = CurrentExpandedHeight - 2;
            AnimateIsland(ExpandedWidth, CurrentExpandedHeight, ExpandedRadius, new SpringEase { Damping = 0.85, Frequency = 12 }, TimeSpan.FromMilliseconds(320), null);
        }

        var any = false;
        foreach (var (id, panel) in Panels)
        {
            var show = tab?.Id == id;
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            any |= show;
        }

        Placeholder.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        UpdatePanelVisibility();
        UpdateIslandBackground();
    }

    /// <summary>Tabs that already have real content (the rest show a placeholder).</summary>
    private IEnumerable<(string Id, FrameworkElement Panel)> Panels =>
    [
        (TabIds.Music, MusicPanel),
        (TabIds.Widgets, WidgetsPanel),
        (TabIds.Clipboard, ClipboardPanel),
        (TabIds.Files, ShelfPanel),
        (TabIds.Apps, AppsPanel),
        (TabIds.QuickSettings, QuickSettingsPanel),
        (TabIds.Notifications, NotificationsPanel),
        (TabIds.Phone, PhonePanel),
        (TabIds.Audio, AudioPanel),
        (TabIds.Web, WebPanel),
    ];

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NotchTab tab })
        {
            return;
        }

        if (tab.Id == TabIds.Settings)
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        Select(tab);
    }

    // ---------------------------------------------------------------- file shelf (drag & drop)

    /// <summary>Files dragged over the notch open the shelf; dropping them puts them on it.</summary>
    private bool AcceptsFiles(DragEventArgs e) =>
        !_shelf.IsDraggingOut && e.Data.GetDataPresent(DataFormats.FileDrop) && _tabs.Any(t => t.Id == TabIds.Files);

    private void OnFileDragEnter(object sender, DragEventArgs e)
    {
        OnFileDragOver(sender, e);
        if (!AcceptsFiles(e))
        {
            return;
        }

        _collapseTimer.Stop();
        _expandTimer.Stop();
        if (SelectedTab?.Id != TabIds.Files && _tabs.FirstOrDefault(t => t.Id == TabIds.Files) is { } files)
        {
            Select(files);
        }

        Expand();
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        // Link: the shelf only remembers where the file is, nothing is copied.
        e.Effects = AcceptsFiles(e) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        if (!_expanded || _shelf.IsDraggingOut)
        {
            return;
        }

        // Moving between elements inside the notch also raises DragLeave; the next DragEnter stops this timer.
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(400, _settings.Current.Notch.CollapseDelayMs));
        _collapseTimer.Start();
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        _collapseTimer.Stop();
        if (AcceptsFiles(e) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _shelf.Add(paths);
            e.Effects = DragDropEffects.Link;
        }

        e.Handled = true;
    }

    // ---------------------------------------------------------------- music

    private bool MusicTabShown => _expanded && IsVisible && SelectedTab?.Id == TabIds.Music;

    private bool TabShown(string id) => _expanded && IsVisible && SelectedTab?.Id == id;

    private void UpdatePanelVisibility()
    {
        _music.SetPanelVisible(MusicTabShown);
        _clipboard.SetVisible(TabShown(TabIds.Clipboard));
        _shelf.SetVisible(TabShown(TabIds.Files));
        _quick.SetVisible(TabShown(TabIds.QuickSettings));
        _notifications?.SetVisible(TabShown(TabIds.Notifications));
    }

    private void OnMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MusicViewModel.IsPlaying):
            case nameof(MusicViewModel.HasMedia):
                UpdateMiniPlayer();
                break;
            case nameof(MusicViewModel.Cover):
                MiniCover.ImageSource = _music.Cover;
                break;
            case nameof(MusicViewModel.Title):
            case nameof(MusicViewModel.ArtistText):
                MiniTitle.Title = _music.Title;
                MiniTitle.Artist = _music.HasMedia && !string.IsNullOrWhiteSpace(_music.ArtistText) ? _music.ArtistText : "";
                break;
            case nameof(MusicViewModel.Accent):
                UpdateIslandBackground();
                break;
            case nameof(MusicViewModel.LyricsOpen):
                UpdateLyricsWindow();
                break;
        }
    }

    private void UpdateMiniPlayer()
    {
        var wanted = _settings.Current.Music.ShowMiniPlayer && _music.HasMedia && _music.IsPlaying;
        if (wanted)
        {
            _miniOffTimer.Stop();
            SetMiniActive(true);
        }
        else if (_miniActive)
        {
            if (!_music.HasMedia || !_settings.Current.Music.ShowMiniPlayer)
            {
                _miniOffTimer.Stop();
                SetMiniActive(false);
            }
            else if (!_miniOffTimer.IsEnabled)
            {
                _miniOffTimer.Start();
            }
        }
    }

    private void SetMiniActive(bool active)
    {
        if (_miniActive == active)
        {
            return;
        }

        _miniActive = active;
        if (!_expanded && !_bannerActive)
        {
            SetHotZone(expanded: false);
            if (IsVisible)
            {
                AnimateIsland(CurrentCollapsedWidth, CollapsedHeight, CollapsedRadius, new SpringEase { Damping = 0.8, Frequency = 11 }, ExpandDuration, null);
                FadeMiniPlayer(show: active, delayMs: active ? 120 : 0);
            }
            else
            {
                ApplyCollapsedImmediately();
            }
        }

        UpdateEqualizer();
    }

    private void FadeMiniPlayer(bool show, int delayMs = 0)
    {
        if (show)
        {
            MiniPlayer.Visibility = Visibility.Visible;
        }

        var a = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 200 : 90)) { BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        a.Completed += (_, _) =>
        {
            var visible = _miniActive && !_expanded;
            MiniPlayer.BeginAnimation(OpacityProperty, null);
            MiniPlayer.Opacity = visible ? 1 : 0;
            MiniPlayer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        };
        MiniPlayer.BeginAnimation(OpacityProperty, a, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Equalizer bars animate (15 fps) only while the mini-player is on screen and animation is enabled.</summary>
    private void UpdateEqualizer(bool force = false)
    {
        UpdateMarquee();
        var run = _miniActive && !_expanded && !_bannerActive && IsVisible && _settings.Current.Music.AnimateMiniPlayer;
        if (run == _equalizerRunning && !force)
        {
            return;
        }

        _equalizerRunning = run;
        for (var i = 0; i < Equalizer.Children.Count; i++)
        {
            if (Equalizer.Children[i] is not Rectangle { RenderTransform: ScaleTransform scale })
            {
                continue;
            }

            if (!run)
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                continue;
            }

            var a = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(EqualizerPeriods[i % EqualizerPeriods.Length]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(-90 * i),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(a, 15);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    /// <summary>The pill title scrolls only while the mini-player is actually on screen.</summary>
    private void UpdateMarquee() =>
        MiniTitle.IsActive = _miniActive && !_expanded && !_bannerActive && IsVisible && _settings.Current.Music.ScrollLongTitles;

    /// <summary>On the music tab the island picks up a glow from the album cover (as in ref/Notch.jpg).</summary>
    private void UpdateIslandBackground()
    {
        if (!_expanded || SelectedTab?.Id != TabIds.Music || _music.Accent is not { } c)
        {
            Island.Background = _defaultBackground;
            return;
        }

        static Color Mix(Color accent, double t, byte alpha)
        {
            const byte baseR = 0x0B, baseG = 0x0B, baseB = 0x0D;
            return Color.FromArgb(alpha,
                (byte)(baseR + ((accent.R - baseR) * t)),
                (byte)(baseG + ((accent.G - baseG) * t)),
                (byte)(baseB + ((accent.B - baseB) * t)));
        }

        Island.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Mix(c, 0.42, 0xF2), 0),
                new(Mix(c, 0.12, 0xF2), 0.55),
                new(Mix(c, 0.0, 0xF2), 1),
            },
            new Point(0, 0),
            new Point(1, 1));
    }

    private void UpdateLyricsWindow()
    {
        var show = _music.LyricsOpen && IsVisible;
        if (show)
        {
            if (_lyricsWindow is null)
            {
                _lyricsWindow = new LyricsWindow(_music);
                _lyricsWindow.MovedByUser += OnLyricsMovedByUser;
                _lyricsWindow.ResetRequested += (_, _) => _settings.Update(s => s.Music.LyricsPosition = null);
            }

            PlaceLyricsWindow();

            if (!_lyricsWindow.IsVisible)
            {
                _lyricsWindow.Show();
            }

            _music.SetLyricsVisible(true);
        }
        else if (_lyricsWindow is { IsVisible: true })
        {
            _lyricsWindow.Hide();
            _music.SetLyricsVisible(false);
        }
    }

    /// <summary>Where the user left the card (if that monitor is still connected), otherwise under the notch.</summary>
    private void PlaceLyricsWindow()
    {
        if (_lyricsWindow is null)
        {
            return;
        }

        if (_settings.Current.Music.LyricsPosition is { } pos &&
            MonitorService.GetMonitors().FirstOrDefault(m => string.Equals(m.DeviceName, pos.Monitor, StringComparison.OrdinalIgnoreCase)) is { } saved)
        {
            _lyricsWindow.PlaceAt(saved, pos.X, pos.Y);
        }
        else if (_monitor is not null)
        {
            _lyricsWindow.PlaceBelowNotch(_monitor, LyricsTop, NotchOffset);
        }
    }

    private void OnLyricsMovedByUser(object? sender, EventArgs e)
    {
        if (_lyricsWindow?.Bounds is not { } r)
        {
            return;
        }

        var centerX = r.Left + (r.Width / 2);
        var centerY = r.Top + (r.Height / 2);
        var monitors = MonitorService.GetMonitors();
        var monitor = monitors.FirstOrDefault(m => centerX >= m.Bounds.Left && centerX < m.Bounds.Right && centerY >= m.Bounds.Top && centerY < m.Bounds.Bottom)
                      ?? NotchLayout.SelectMonitor(monitors, null);
        if (monitor is null)
        {
            return;
        }

        var (x, y) = NotchLayout.OffsetOf(r, monitor);
        _settings.Update(s => s.Music.LyricsPosition = new CardPosition { Monitor = monitor.DeviceName, X = x, Y = y });
    }

    // ---------------------------------------------------------------- settings / language

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var enabled = _settings.Current.Tabs.Where(t => t.Enabled).Select(t => t.Id);
        if (!enabled.SequenceEqual(_tabs.Select(t => t.Id)))
        {
            RebuildTabs();
        }

        ApplyTopOffset();
        SetHotZone(_expanded);
        Reposition();
        _fullscreen.Evaluate();
        UpdateVisibility();
        UpdateMiniPlayer();
        UpdateEqualizer(force: true); // the animation setting may have changed
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var t in _tabs)
        {
            t.RefreshTexts();
        }
    }
}
