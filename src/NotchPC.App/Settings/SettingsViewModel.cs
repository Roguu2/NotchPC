using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NotchPC.App.Infrastructure;
using NotchPC.App.Notch;
using NotchPC.Core.Localization;
using NotchPC.Core.Settings;
using NotchPC.Platform.Monitors;
using NotchPC.Platform.Startup;

namespace NotchPC.App.Settings;

public sealed record SelectOption(string Value, string Display)
{
    /// <summary>Used by screen readers / UI Automation as the item name.</summary>
    public override string ToString() => Display;
}

/// <summary>State of the settings window; every change is applied live through <see cref="SettingsService"/>.</summary>
internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settings;

    public SettingsViewModel(SettingsService settings, System.Net.Http.HttpClient http, Widgets.SensorsClient sensors, Clipboard.ClipboardService clipboard, Apps.CaptureService capture, Notifications.NotificationHub hub, Games.GameOverlayController overlay, Remote.RemoteServer remote, Updates.UpdateService updates)
    {
        _settings = settings;
        Notify = new NotificationSettingsViewModel(settings, hub);
        Overlay = new GameOverlaySettingsViewModel(settings, overlay);
        Remote = new RemoteSettingsViewModel(settings, remote);
        Updates = new UpdatesViewModel(settings, updates);
        Widgets = new WidgetSettingsViewModel(settings, http, sensors);
        Capture = new CaptureSettingsViewModel(settings, clipboard, capture);
        MoveUpCommand = new RelayCommand<TabRow>(row => Move(row, -1), row => row is not null && Tabs.IndexOf(row) > 0);
        MoveDownCommand = new RelayCommand<TabRow>(row => Move(row, +1), row => row is not null && Tabs.IndexOf(row) < Tabs.Count - 1);
        Rebuild();
        Loc.CultureChanged += (_, _) => Rebuild();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The "Desktop widgets" section.</summary>
    public WidgetSettingsViewModel Widgets { get; }

    /// <summary>The "Clipboard and screenshots" section.</summary>
    public CaptureSettingsViewModel Capture { get; }

    /// <summary>The "Notifications" section.</summary>
    public NotificationSettingsViewModel Notify { get; }

    /// <summary>The "Game overlay" section.</summary>
    public GameOverlaySettingsViewModel Overlay { get; }

    /// <summary>The "Phone remote" section.</summary>
    public RemoteSettingsViewModel Remote { get; }

    /// <summary>Automatic updates.</summary>
    public UpdatesViewModel Updates { get; }

    public ObservableCollection<SelectOption> Languages { get; } = [];

    public ObservableCollection<SelectOption> Monitors { get; } = [];

    public ObservableCollection<TabRow> Tabs { get; } = [];

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public string VersionText { get; private set; } = "";

    public string SettingsFolder => System.IO.Path.GetDirectoryName(_settings.FilePath) ?? "";

    // Combo boxes bind to the option instances (SelectedItem): rebuilding the option lists while bound
    // by value (SelectedValue) left the selection empty.
    public SelectOption? SelectedLanguage
    {
        get => Languages.FirstOrDefault(o => o.Value == _settings.Current.Language);
        set
        {
            if (value is null || value.Value == _settings.Current.Language)
            {
                return;
            }

            _settings.Update(s => s.Language = value.Value);
            Loc.SetCulture(Loc.Resolve(value.Value));
            OnPropertyChanged();
        }
    }

    public SelectOption? SelectedMonitor
    {
        get => Monitors.FirstOrDefault(o => o.Value == _settings.Current.Notch.Monitor);
        set
        {
            if (value is null || value.Value == _settings.Current.Notch.Monitor)
            {
                return;
            }

            _settings.Update(s => s.Notch.Monitor = value.Value);
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => _settings.Current.StartWithWindows;
        set
        {
            AutostartService.Set(value);
            _settings.Update(s => s.StartWithWindows = value);
            OnPropertyChanged();
        }
    }

    public bool ShowMiniPlayer
    {
        get => _settings.Current.Music.ShowMiniPlayer;
        set
        {
            _settings.Update(s => s.Music.ShowMiniPlayer = value);
            OnPropertyChanged();
        }
    }

    public bool ScrollLongTitles
    {
        get => _settings.Current.Music.ScrollLongTitles;
        set
        {
            _settings.Update(s => s.Music.ScrollLongTitles = value);
            OnPropertyChanged();
        }
    }

    public bool AnimateMiniPlayer
    {
        get => _settings.Current.Music.AnimateMiniPlayer;
        set
        {
            _settings.Update(s => s.Music.AnimateMiniPlayer = value);
            OnPropertyChanged();
        }
    }

    public bool LyricsMoved => _settings.Current.Music.LyricsPosition is not null;

    public void ResetLyricsPosition()
    {
        _settings.Update(s => s.Music.LyricsPosition = null);
        OnPropertyChanged(nameof(LyricsMoved));
    }

    /// <summary>Called when the window is shown: the card may have been dragged since.</summary>
    public void RefreshLyricsMoved() => OnPropertyChanged(nameof(LyricsMoved));

    public bool HideOnFullscreen
    {
        get => _settings.Current.Notch.HideOnFullscreen;
        set
        {
            _settings.Update(s => s.Notch.HideOnFullscreen = value);
            OnPropertyChanged();
        }
    }

    public bool TaskbarPill
    {
        get => _settings.Current.Notch.TaskbarPill;
        set
        {
            _settings.Update(s => s.Notch.TaskbarPill = value);
            OnPropertyChanged();
        }
    }

    /// <summary>The notch was dragged away from the centre (or onto another monitor than the primary).</summary>
    public bool NotchMoved => Math.Abs(_settings.Current.Notch.HorizontalOffset) > 0.5;

    public double TopOffset
    {
        get => _settings.Current.Notch.TopOffset;
        set
        {
            var v = (int)Math.Round(value);
            if (v == _settings.Current.Notch.TopOffset)
            {
                return;
            }

            _settings.Update(s => s.Notch.TopOffset = v);
            OnPropertyChanged();
            OnPropertyChanged(nameof(TopOffsetText));
        }
    }

    public string TopOffsetText => $"{_settings.Current.Notch.TopOffset} px";

    internal SettingsService Settings => _settings;

    public void CenterNotch() => _settings.Update(s => s.Notch.HorizontalOffset = 0);

    public void RefreshNotchPosition()
    {
        OnPropertyChanged(nameof(NotchMoved));
        OnPropertyChanged(nameof(TopOffset));
        OnPropertyChanged(nameof(TopOffsetText));
    }

    public double HoverDelay
    {
        get => _settings.Current.Notch.HoverDelayMs;
        set
        {
            var ms = (int)Math.Round(value / 10) * 10;
            if (ms == _settings.Current.Notch.HoverDelayMs)
            {
                return;
            }

            _settings.Update(s => s.Notch.HoverDelayMs = ms);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HoverDelayText));
        }
    }

    public string HoverDelayText => Loc.Format("Settings.Milliseconds", _settings.Current.Notch.HoverDelayMs);

    public double CollapseDelay
    {
        get => _settings.Current.Notch.CollapseDelayMs;
        set
        {
            var ms = (int)Math.Round(value / 10) * 10;
            if (ms == _settings.Current.Notch.CollapseDelayMs)
            {
                return;
            }

            _settings.Update(s => s.Notch.CollapseDelayMs = ms);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CollapseDelayText));
        }
    }

    public string CollapseDelayText => Loc.Format("Settings.Milliseconds", _settings.Current.Notch.CollapseDelayMs);

    /// <summary>Re-reads monitors (they may have been plugged in/out since the window opened).</summary>
    public void RefreshMonitors()
    {
        var current = _settings.Current.Notch.Monitor;
        var options = new List<SelectOption> { new("", Loc.Get("Settings.Monitor.Primary")) };
        foreach (var m in MonitorService.GetMonitors().OrderBy(m => m.Bounds.Left))
        {
            var name = m.DeviceName.Replace(@"\\.\", "", StringComparison.Ordinal);
            var text = Loc.Format("Settings.Monitor.Item", name, m.Bounds.Width, m.Bounds.Height, (int)Math.Round(m.Scale * 100));
            if (m.IsPrimary)
            {
                text += " " + Loc.Get("Settings.Monitor.PrimarySuffix");
            }

            options.Add(new SelectOption(m.DeviceName, text));
        }

        if (!string.IsNullOrEmpty(current) && options.All(o => o.Value != current))
        {
            // Configured monitor is unplugged: keep the setting (it comes back when replugged) but show it.
            options.Add(new SelectOption(current, current.Replace(@"\\.\", "", StringComparison.Ordinal)));
        }

        if (options.SequenceEqual(Monitors))
        {
            return; // unchanged: do not disturb an open drop-down
        }

        Monitors.Clear();
        foreach (var o in options)
        {
            Monitors.Add(o);
        }

        OnPropertyChanged(nameof(SelectedMonitor));
    }

    private void Rebuild()
    {
        Languages.Clear();
        Languages.Add(new SelectOption("", Loc.Get("Settings.Language.System")));
        foreach (var c in Loc.SupportedCultures)
        {
            var native = c.NativeName;
            Languages.Add(new SelectOption(c.Name, char.ToUpper(native[0], c) + native[1..]));
        }

        OnPropertyChanged(nameof(SelectedLanguage));

        RefreshMonitors();

        Tabs.Clear();
        foreach (var t in _settings.Current.Tabs)
        {
            Tabs.Add(new TabRow(this, t.Id, t.Enabled));
        }

        OnPropertyChanged(nameof(HoverDelayText));
        OnPropertyChanged(nameof(CollapseDelayText));
        VersionText = Loc.Format("Settings.Version", typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");
        OnPropertyChanged(nameof(VersionText));
    }

    internal void SetTabEnabled(string id, bool enabled)
    {
        _settings.Update(s =>
        {
            var i = s.Tabs.FindIndex(t => t.Id == id);
            s.Tabs[i] = s.Tabs[i] with { Enabled = enabled };
        });
    }

    private void Move(TabRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        var from = Tabs.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Tabs.Count)
        {
            return;
        }

        Tabs.Move(from, to);
        _settings.Update(s =>
        {
            var item = s.Tabs[from];
            s.Tabs.RemoveAt(from);
            s.Tabs.Insert(to, item);
        });
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class TabRow : INotifyPropertyChanged
{
    private readonly SettingsViewModel _owner;
    private bool _enabled;

    public TabRow(SettingsViewModel owner, string id, bool enabled)
    {
        _owner = owner;
        Id = id;
        _enabled = enabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Glyph => TabCatalog.GlyphFor(Id);

    public string Label => Loc.Get("Tab." + Id);

    /// <summary>The settings tab cannot be turned off (it is the way back into this window).</summary>
    public bool CanToggle => Id != TabIds.Settings;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value || !CanToggle)
            {
                return;
            }

            _enabled = value;
            _owner.SetTabEnabled(Id, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }
}

internal sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute(parameter is T t ? t : default);

    public void Execute(object? parameter) => execute(parameter is T t ? t : default);
}
