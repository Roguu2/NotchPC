using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NotchPC.App.Infrastructure;
using NotchPC.App.Music;
using NotchPC.Core.Layout;
using NotchPC.Core.Settings;
using NotchPC.Platform.Monitors;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Notch;

/// <summary>
/// Experiment (R8): a small now-playing pill laid over the taskbar, just left of the clock. Windows 11 has no API
/// to add something to the taskbar, so this is a topmost window on top of it; after a click on the taskbar Windows
/// raises the taskbar again, so the pill is lifted back on every foreground change. Only for a bottom taskbar that
/// is not auto-hidden, only while something plays, hidden in fullscreen. Clicking it opens the music tab.
/// </summary>
internal sealed class TaskbarPill : IDisposable
{
    private const double WidthDip = 260;
    private const double HeightDip = 36;
    private const double GapDip = 10;

    private readonly SettingsService _settings;
    private readonly MusicViewModel _music;
    private readonly FullscreenWatcher _watcher;
    private readonly Action _openMusic;
    private Window? _window;
    private IntPtr _hwnd;
    private Image? _cover;
    private TextBlock? _title;
    private Button? _play;

    public TaskbarPill(SettingsService settings, MusicViewModel music, FullscreenWatcher watcher, Action openMusic)
    {
        _settings = settings;
        _music = music;
        _watcher = watcher;
        _openMusic = openMusic;
        _settings.Changed += OnChanged;
        _music.PropertyChanged += OnMusicChanged;
        _watcher.Evaluated += OnChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnChanged;
        Update();
    }

    public void Dispose()
    {
        _settings.Changed -= OnChanged;
        _music.PropertyChanged -= OnMusicChanged;
        _watcher.Evaluated -= OnChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnChanged;
        _window?.Close();
        _window = null;
    }

    private void OnChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(Update);

    private void OnMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicViewModel.HasMedia) or nameof(MusicViewModel.Title) or nameof(MusicViewModel.ArtistText)
            or nameof(MusicViewModel.Cover) or nameof(MusicViewModel.IsPlaying))
        {
            Update();
        }
    }

    private void Update()
    {
        var place = _settings.Current.Notch.TaskbarPill && _music.HasMedia && !_watcher.IsFullscreen ? Placement() : null;
        if (place is not { } rect)
        {
            _window?.Hide();
            return;
        }

        EnsureWindow();
        _cover!.Source = _music.Cover;
        _title!.Text = string.IsNullOrWhiteSpace(_music.ArtistText) ? _music.Title : $"{_music.Title} · {_music.ArtistText}";
        _play!.Content = _music.PlayPauseGlyph;
        if (!_window!.IsVisible)
        {
            _window.Show();
        }

        WindowHelper.PlaceTopmost(_hwnd, rect); // also lifts it above the taskbar again
    }

    /// <summary>Left of the notification area on the notch monitor's taskbar (physical pixels), or null.</summary>
    private ScreenRect? Placement()
    {
        var monitors = MonitorService.GetMonitors();
        if (NotchLayout.SelectMonitor(monitors, _settings.Current.Notch.Monitor) is not { } m
            || Taskbar.On(m.Bounds) is not { } bar || bar.AutoHide || !bar.IsBottom(m.Bounds))
        {
            return null;
        }

        var height = (int)Math.Min(bar.Bounds.Height - (8 * m.Scale), HeightDip * m.Scale);
        var width = (int)(WidthDip * m.Scale);
        var right = (bar.TrayLeft ?? (bar.Bounds.Right - (int)(170 * m.Scale))) - (int)(GapDip * m.Scale);
        var top = bar.Bounds.Top + ((bar.Bounds.Height - height) / 2);
        return height <= 0 ? null : ScreenRect.FromSize(right - width, top, width, height);
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _cover = new Image { Width = 22, Height = 22, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 22, 22), 6, 6) };
        _title = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
        };
        _play = new Button
        {
            Style = (Style)Application.Current.FindResource("NotchSmallIconButton"),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _play.Click += (_, _) => _ = _music.PlayPauseAsync();

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(_play, 2);
        row.Children.Add(_cover);
        row.Children.Add(_title);
        row.Children.Add(_play);

        var pill = new Border
        {
            Background = (Brush)Application.Current.FindResource("IslandBackground"),
            BorderBrush = (Brush)Application.Current.FindResource("IslandBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(7, 0, 4, 0),
            Child = row,
            Cursor = Cursors.Hand,
            ToolTip = Core.Localization.Loc.Get("Taskbar.Pill.Hint"),
        };
        pill.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d) is null)
            {
                _openMusic();
            }
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Title = "NotchPC taskbar",
            Content = pill,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        };
        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        WindowHelper.MakeOverlay(_hwnd, noActivate: true);
    }

    private static Button? FindButton(DependencyObject? d)
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Button b)
            {
                return b;
            }
        }

        return null;
    }
}
