using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotchPC.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Saving is atomic (temp file + replace).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchPC");

    public string FilePath { get; }

    /// <summary>
    /// Reads settings. A missing file gives defaults; a corrupt file is renamed to *.corrupt-&lt;timestamp&gt;
    /// so the user does not lose it, and defaults are returned.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return Normalize(new AppSettings());
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            return Normalize(settings);
        }
        catch (JsonException)
        {
            BackupCorruptFile();
            return Normalize(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Fixes values that may come from an older or hand-edited file.</summary>
    internal static AppSettings Normalize(AppSettings s)
    {
        s.SchemaVersion = AppSettings.CurrentSchemaVersion;
        s.Language ??= "";
        s.Notch ??= new NotchSettings();
        s.Music ??= new MusicSettings();
        if (s.Music.LyricsPosition is { } p &&
            (string.IsNullOrWhiteSpace(p.Monitor) || !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
        {
            s.Music.LyricsPosition = null;
        }
        s.Notch.Monitor ??= "";
        s.Notch.HoverDelayMs = Math.Clamp(s.Notch.HoverDelayMs, 0, 2000);
        s.Notch.CollapseDelayMs = Math.Clamp(s.Notch.CollapseDelayMs, 0, 5000);
        s.Notch.TopOffset = Math.Clamp(s.Notch.TopOffset, 0, 64);
        s.Clipboard ??= new ClipboardSettings();
        s.Clipboard.Limit = Math.Clamp(s.Clipboard.Limit, 10, 500);
        s.Shelf ??= new ShelfSettings();
        s.Shelf.Files = (s.Shelf.Files ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.Apps ??= new AppsSettings();
        s.Apps.ScreenshotFolder ??= "";
        s.Apps.ShareXPath ??= "";
        s.Apps.Shortcuts = s.Apps.Shortcuts?.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Target)).ToList();
        s.Notifications ??= new NotificationSettings();
        s.Notifications.BannerSeconds = Math.Clamp(s.Notifications.BannerSeconds, 2, 30);
        s.Notifications.CalendarReminderMinutes = Math.Clamp(s.Notifications.CalendarReminderMinutes, 0, 120);
        s.Notifications.Sound = NotchPC.Core.Notifications.NotificationSounds.Normalize(s.Notifications.Sound);
        s.Notifications.SoundVolume = Math.Clamp(s.Notifications.SoundVolume, 0, 100);
        s.Notifications.GlowColor = NotchPC.Core.Notifications.GlowColors.Normalize(s.Notifications.GlowColor);
        s.GameOverlay ??= new GameOverlaySettings();
        s.GameOverlay.Position = NotchPC.Core.Games.OverlayPositions.Normalize(s.GameOverlay.Position);
        s.GameOverlay.Hotkey = NotchPC.Core.Games.OverlayHotkeys.Normalize(s.GameOverlay.Hotkey);
        s.GameOverlay.Games = NotchPC.Core.Games.GameDetection.Normalize(s.GameOverlay.Games);
        s.GameOverlay.Ignored = NotchPC.Core.Games.GameDetection.Normalize(s.GameOverlay.Ignored);
        s.Updates ??= new UpdateSettings();
        s.Theme ??= new ThemeSettings();
        s.Theme.Style = ThemeStyles.Normalize(s.Theme.Style);
        s.Theme.Accent = ThemeAccents.Normalize(s.Theme.Accent);
        s.Theme.Corners = ThemeCorners.Normalize(s.Theme.Corners);
        s.Theme.Opacity = Math.Clamp(s.Theme.Opacity, 60, 100);
        s.Web ??= new WebSettings();
        s.Web.Width = Math.Clamp(s.Web.Width, 480, 2400);
        s.Web.Height = Math.Clamp(s.Web.Height, 360, 1600);
        s.Web.Bookmarks = s.Web.Bookmarks?
            .Where(b => b is not null && NotchPC.Core.Web.WebAddress.IsWeb(b.Url))
            .ToList();
        s.Remote ??= new RemoteSettings();
        s.Remote.Port = s.Remote.Port is >= 1024 and <= 65535 ? s.Remote.Port : 47800;
        s.Remote.PowerDelaySeconds = Math.Clamp(s.Remote.PowerDelaySeconds, 0, 600);
        s.Remote.Devices = (s.Remote.Devices ?? [])
            .Where(d => d is not null && d.Id.Length > 0 && d.TokenHash.Length == 64)
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        s.Widgets ??= new WidgetSettings();
        s.Widgets.FocusMinutes = Math.Clamp(s.Widgets.FocusMinutes, 5, 120);
        s.Widgets.BreakMinutes = Math.Clamp(s.Widgets.BreakMinutes, 5, 120);
        s.Widgets.GridCell = Math.Clamp(s.Widgets.GridCell, 8, 64);
        s.Widgets.GridMargin = Math.Clamp(s.Widgets.GridMargin, 0, 120);
        if (s.Widgets.LayoutMode is not (WidgetLayoutModes.Grid or WidgetLayoutModes.Free))
        {
            s.Widgets.LayoutMode = WidgetLayoutModes.Grid;
        }
        s.Widgets.Positions = (s.Widgets.Positions ?? [])
            .Where(kv => kv.Value is { } p && !string.IsNullOrWhiteSpace(p.Monitor) && double.IsFinite(p.X) && double.IsFinite(p.Y))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (s.Widgets.WeatherLatitude is null || s.Widgets.WeatherLongitude is null)
        {
            s.Widgets.WeatherLatitude = null;
            s.Widgets.WeatherLongitude = null;
        }

        s.Widgets.GameFavorites ??= [];
        s.Widgets.GameHidden ??= [];
        s.Widgets.GameOrder ??= [];
        s.Widgets.GameSort = NotchPC.Core.Games.GameSortModes.Normalize(s.Widgets.GameSort);

        // Tabs that became desktop widgets: a tab that was on becomes a widget that is on (once: the ids go away below).
        foreach (var tab in s.Tabs ?? [])
        {
            if (tab is { Enabled: true, Id: TabIds.RetiredApps })
            {
                s.Widgets.AppsEnabled = true;
            }
            else if (tab is { Enabled: true, Id: TabIds.RetiredGames })
            {
                s.Widgets.GamesEnabled = true;
            }
        }

        s.Tabs = NormalizeTabs(s.Tabs);
        return s;
    }

    internal static List<TabSetting> NormalizeTabs(IEnumerable<TabSetting>? tabs)
    {
        var known = new HashSet<string>(TabIds.DefaultOrder, StringComparer.Ordinal);
        var result = new List<TabSetting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tab in tabs ?? [])
        {
            if (tab?.Id is { } id && known.Contains(id) && seen.Add(id))
            {
                result.Add(tab);
            }
        }

        foreach (var id in TabIds.DefaultOrder)
        {
            if (seen.Add(id))
            {
                // A tab added in a newer version goes before the settings gear (which stays last), not after it.
                var gear = result.FindIndex(t => t.Id == TabIds.Settings);
                if (gear >= 0 && id != TabIds.Settings)
                {
                    result.Insert(gear, new TabSetting(id, true));
                }
                else
                {
                    result.Add(new TabSetting(id, true));
                }
            }
        }

        // The settings tab is the only way back into configuration from the notch: keep it on.
        var settingsIndex = result.FindIndex(t => t.Id == TabIds.Settings);
        result[settingsIndex] = result[settingsIndex] with { Enabled = true };
        return result;
    }

    private void BackupCorruptFile()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(FilePath, $"{FilePath}.corrupt-{stamp}", overwrite: true);
        }
        catch (IOException)
        {
            // Keep going with defaults; the next save overwrites the broken file.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
