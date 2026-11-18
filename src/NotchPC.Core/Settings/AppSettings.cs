namespace NotchPC.Core.Settings;

/// <summary>All user settings persisted to %AppData%\NotchPC\settings.json.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Culture name ("pl", "en") or empty for the Windows display language.</summary>
    public string Language { get; set; } = "";

    public bool StartWithWindows { get; set; }

    public NotchSettings Notch { get; set; } = new();

    public MusicSettings Music { get; set; } = new();

    public WidgetSettings Widgets { get; set; } = new();

    public ClipboardSettings Clipboard { get; set; } = new();

    public ShelfSettings Shelf { get; set; } = new();

    public AppsSettings Apps { get; set; } = new();

    public NotificationSettings Notifications { get; set; } = new();

    public GameOverlaySettings GameOverlay { get; set; } = new();

    public RemoteSettings Remote { get; set; } = new();

    public WebSettings Web { get; set; } = new();

    /// <summary>Tabs in display order. Unknown ids are dropped and missing ones appended on load.</summary>
    public List<TabSetting> Tabs { get; set; } = TabIds.DefaultOrder.Select(id => new TabSetting(id, true)).ToList();
}

public sealed class NotchSettings
{
    /// <summary>Device name of the target monitor (e.g. \\.\DISPLAY1); empty = primary monitor.</summary>
    public string Monitor { get; set; } = "";

    /// <summary>How long the cursor has to rest on the pill before it expands.</summary>
    public int HoverDelayMs { get; set; } = 120;

    /// <summary>How long the cursor has to be away before the panel collapses.</summary>
    public int CollapseDelayMs { get; set; } = 350;

    public bool HideOnFullscreen { get; set; } = true;

    /// <summary>Experiment: a now-playing pill over the taskbar, left of the clock.</summary>
    public bool TaskbarPill { get; set; }

    /// <summary>Gap between the top edge of the screen and the pill, in DIPs.</summary>
    public int TopOffset { get; set; } = 6;
}

public sealed class MusicSettings
{
    /// <summary>Widen the collapsed pill to show the cover and an equalizer while music plays.</summary>
    public bool ShowMiniPlayer { get; set; } = true;

    /// <summary>Animate the pill equalizer (costs a little CPU while playing); off = static bars.</summary>
    public bool AnimateMiniPlayer { get; set; } = true;

    /// <summary>Scroll a title that does not fit in the pill (a few passes after each track change).</summary>
    public bool ScrollLongTitles { get; set; } = true;

    /// <summary>The lyrics card was open when the app last ran.</summary>
    public bool LyricsOpen { get; set; }

    /// <summary>Where the user dragged the lyrics card; null = under the notch.</summary>
    public CardPosition? LyricsPosition { get; set; }
}

/// <summary>Desktop widgets (stage 3).</summary>
public sealed class WidgetSettings
{
    public bool TimerEnabled { get; set; } = true;

    public bool WeatherEnabled { get; set; } = true;

    public bool CalendarEnabled { get; set; } = true;

    public bool StatsEnabled { get; set; } = true;

    /// <summary>Twitch chat card (off by default: it needs a channel name).</summary>
    public bool TwitchEnabled { get; set; }

    /// <summary>Channel whose chat the card shows (login name, e.g. "some_streamer").</summary>
    public string TwitchChannel { get; set; } = "";

    /// <summary>Widgets cannot be dragged (no accidental moves).</summary>
    public bool Locked { get; set; }

    /// <summary><see cref="WidgetLayoutModes.Grid"/> (snap to cells, no overlaps) or <see cref="WidgetLayoutModes.Free"/>.</summary>
    public string LayoutMode { get; set; } = WidgetLayoutModes.Grid;

    /// <summary>Grid cell size in DIPs.</summary>
    public int GridCell { get; set; } = 16;

    /// <summary>Distance between the grid and the edges of the screen's work area, in DIPs.</summary>
    public int GridMargin { get; set; } = 24;

    /// <summary>Show the dotted grid while a widget is being moved or resized.</summary>
    public bool ShowGridWhileMoving { get; set; } = true;

    public int FocusMinutes { get; set; } = 25;

    public int BreakMinutes { get; set; } = 5;

    /// <summary>Chosen place for the weather; null until the user picks a city.</summary>
    public string? WeatherPlace { get; set; }

    public double? WeatherLatitude { get; set; }

    public double? WeatherLongitude { get; set; }

    /// <summary>What a large weather widget shows below the current conditions: "hourly" or "daily".</summary>
    public string WeatherForecast { get; set; } = "hourly";

    /// <summary>Day (yyyy-MM-dd) the focus counters below belong to; they reset on a new day.</summary>
    public string? FocusStatsDay { get; set; }

    public int FocusSessionsToday { get; set; }

    public int FocusMinutesToday { get; set; }

    /// <summary>iCal link encrypted with DPAPI for the current Windows user (base64); never stored in plain text.</summary>
    public string? CalendarUrlProtected { get; set; }

    /// <summary>Where each widget was dragged (key = widget id); missing = default stack on the primary monitor.</summary>
    public Dictionary<string, CardPosition> Positions { get; set; } = [];
}

/// <summary>Clipboard history (stage 4). Stored encrypted for the current Windows user.</summary>
public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum number of entries (pinned ones do not count against it being trimmed).</summary>
    public int Limit { get; set; } = 100;
}

/// <summary>File shelf: files dropped on the notch. Only paths are kept; files are never copied.</summary>
public sealed class ShelfSettings
{
    public List<string> Files { get; set; } = [];
}

/// <summary>Apps tab: capture options and app shortcuts.</summary>
public sealed class AppsSettings
{
    /// <summary>Where screenshots are saved; empty = Pictures\Screenshots.</summary>
    public string ScreenshotFolder { get; set; } = "";

    /// <summary>ShareX.exe used for screen recording; empty = detect.</summary>
    public string ShareXPath { get; set; } = "";

    /// <summary>App shortcuts in display order (exe, lnk, url or any file / URI). Null = defaults on first run.</summary>
    public List<AppShortcut>? Shortcuts { get; set; }
}

public sealed class AppShortcut
{
    public string Name { get; set; } = "";

    public string Target { get; set; } = "";
}

/// <summary>Notifications (stage 6).</summary>
public sealed class NotificationSettings
{
    /// <summary>NotchPC's own notifications (timer, calendar) pop up in the notch.</summary>
    public bool ShowAppBanners { get; set; } = true;

    /// <summary>New Windows notifications of other apps pop up in the notch (needs package identity).</summary>
    public bool ShowSystemBanners { get; set; } = true;

    /// <summary>How long a banner stays, in seconds.</summary>
    public int BannerSeconds { get; set; } = 5;

    /// <summary>No banners while Windows says the user is busy (fullscreen, presentation, Do not disturb).</summary>
    public bool RespectBusy { get; set; } = true;

    /// <summary>Minutes before a calendar event to remind; 0 = off.</summary>
    public int CalendarReminderMinutes { get; set; } = 10;

    /// <summary>Sound played with a banner (see NotificationSounds): chime, drop, glass or none.</summary>
    public string Sound { get; set; } = "chime";

    /// <summary>Sound volume, 0–100 (on top of NotchPC's volume in the Windows mixer).</summary>
    public int SoundVolume { get; set; } = 60;

    /// <summary>A coloured glow around the banner.</summary>
    public bool Glow { get; set; } = true;

    /// <summary>Glow colour: "accent" (Windows accent colour) or #RRGGBB.</summary>
    public string GlowColor { get; set; } = "accent";
}

/// <summary>Web panel (stage 9).</summary>
public sealed class WebSettings
{
    /// <summary>Tiles in the notch's web tab. Null = never set (defaults are filled in once).</summary>
    public List<WebBookmark>? Bookmarks { get; set; }

    /// <summary>Panel size in DIPs.</summary>
    public double Width { get; set; } = 980;

    public double Height { get; set; } = 680;

    /// <summary>Hide the panel when another window is clicked.</summary>
    public bool HideOnDeactivate { get; set; } = true;
}

public sealed class WebBookmark
{
    public string Name { get; set; } = "";

    public string Url { get; set; } = "";
}

/// <summary>Phone remote (stage 8).</summary>
public sealed class RemoteSettings
{
    /// <summary>Off until the user turns it on: it opens a port in the home network.</summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = 47800;

    /// <summary>Shutdown / restart from the phone waits this long (and can be cancelled), seconds.</summary>
    public int PowerDelaySeconds { get; set; } = 30;

    public List<PairedDevice> Devices { get; set; } = [];
}

public sealed class PairedDevice
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the device token (the token itself is only on the phone).</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset PairedAt { get; set; }

    public DateTimeOffset? LastSeen { get; set; }
}

/// <summary>Game overlay (stage 7).</summary>
public sealed class GameOverlaySettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Show for any app whose window covers its monitor (except the ignored ones).</summary>
    public bool AutoFullscreen { get; set; } = true;

    /// <summary>top-left, top-right, bottom-left, bottom-right (of the game's monitor).</summary>
    public string Position { get; set; } = "top-left";

    /// <summary>Show / hide shortcut, see OverlayHotkeys.</summary>
    public string Hotkey { get; set; } = "ctrl+alt+f";

    public bool ShowClock { get; set; } = true;

    /// <summary>Process names (without .exe) that always get the overlay, also windowed.</summary>
    public List<string> Games { get; set; } = [];

    /// <summary>Process names that never get it (browsers and players by default).</summary>
    public List<string> Ignored { get; set; } = [.. NotchPC.Core.Games.GameDetection.DefaultIgnored];
}

public static class WidgetLayoutModes
{
    public const string Grid = "grid";
    public const string Free = "free";
}

/// <summary>Top-left (and optionally size) of a floating card, in DIPs relative to the top-left of a monitor.</summary>
public sealed class CardPosition
{
    /// <summary>Device name of the monitor (\\.\DISPLAYn).</summary>
    public string Monitor { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Size chosen by the user (DIPs); null = the card's natural size.</summary>
    public double? Width { get; set; }

    public double? Height { get; set; }
}

public sealed record TabSetting(string Id, bool Enabled);
