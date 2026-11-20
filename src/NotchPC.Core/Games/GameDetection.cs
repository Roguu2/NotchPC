using System.Globalization;
using NotchPC.Core.Layout;

namespace NotchPC.Core.Games;

/// <summary>Decides when the game overlay shows. Pure logic (process names compared without case and ".exe").</summary>
public static class GameDetection
{
    /// <summary>Fullscreen apps that are not games: browsers (fullscreen video), players, presentations, remote desktop.</summary>
    public static IReadOnlyList<string> DefaultIgnored { get; } =
    [
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "arc",
        "vlc", "mpc-hc64", "mpc-be64", "PotPlayerMini64", "Microsoft.Media.Player", "Video.UI",
        "POWERPNT", "mstsc", "msrdc", "explorer", "ApplicationFrameHost", "SnippingTool", "ShareX",
    ];

    /// <summary>Windows' own screens (lock screen, sign-in, Start, search …): never a game, whatever the lists say.</summary>
    private static readonly HashSet<string> SystemApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "LockApp", "LogonUI", "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "ApplicationFrameHost", "TextInputHost", "ScreenClippingHost", "SystemSettings", "dwm", "consent",
    };

    /// <summary>Everyday apps that are sometimes fullscreen but are not games: only when the user listed them.</summary>
    private static readonly HashSet<string> NotAutoDetected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "claude", "Spotify", "ms-teams", "Teams", "Zoom", "slack", "WhatsApp", "Telegram", "Code",
        "devenv", "rider64", "Notion", "obs64", "steamwebhelper", "EpicGamesLauncher", "Battle.net", "RiotClientUx",
    };

    public static string Clean(string? name)
    {
        var n = (name ?? "").Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4].Trim() : n;
    }

    /// <summary>
    /// Ignored apps never; listed games always (also windowed); anything else when its window covers its monitor
    /// and automatic detection is on.
    /// </summary>
    public static bool IsGame(string process, bool coversMonitor, bool autoFullscreen, IEnumerable<string> games, IEnumerable<string> ignored)
    {
        var name = Clean(process);
        if (name.Length == 0 || SystemApps.Contains(name) || Contains(ignored, name))
        {
            return false;
        }

        return Contains(games, name) || (autoFullscreen && coversMonitor && !NotAutoDetected.Contains(name));
    }

    /// <summary>
    /// The shortcut: hides the overlay for an app where it shows (it goes to the ignored list) or shows it where
    /// it does not (the app goes to the games list). Returns the new lists.
    /// </summary>
    public static (List<string> Games, List<string> Ignored) Toggle(string process, bool shownNow, IEnumerable<string> games, IEnumerable<string> ignored)
    {
        var name = Clean(process);
        var g = games.Where(x => !Same(x, name)).ToList();
        var i = ignored.Where(x => !Same(x, name)).ToList();
        if (name.Length > 0)
        {
            (shownNow ? i : g).Add(name);
        }

        return (g, i);
    }

    /// <summary>Cleaned, without empty entries and duplicates (first spelling wins).</summary>
    public static List<string> Normalize(IEnumerable<string>? names) =>
        (names ?? []).Select(Clean).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool Contains(IEnumerable<string> list, string name) => list.Any(x => Same(x, name));

    private static bool Same(string a, string b) => string.Equals(Clean(a), b, StringComparison.OrdinalIgnoreCase);
}

public enum FpsLevel
{
    None,
    Low,
    Fair,
    Good,
}

/// <summary>How values on the overlay are written.</summary>
public static class OverlayFormat
{
    public static string Fps(double? fps) =>
        fps is { } f && f >= 0.5 ? Math.Round(f).ToString("0", CultureInfo.InvariantCulture) : "—";

    /// <summary>Colour band: below 30 low, 30–59 fair, 60+ good.</summary>
    public static FpsLevel Level(double? fps) => fps switch
    {
        null or < 0.5 => FpsLevel.None,
        < 30 => FpsLevel.Low,
        < 59.5 => FpsLevel.Fair,
        _ => FpsLevel.Good,
    };

    /// <summary>"23% · 54°", or "23%" without a temperature, "—" without a load.</summary>
    public static string LoadAndTemp(double? load, double? temp)
    {
        var c = CultureInfo.InvariantCulture;
        var l = load is { } x ? Math.Round(x).ToString("0", c) + "%" : "—";
        return temp is { } t ? $"{l} · {Math.Round(t).ToString("0", c)}°" : l;
    }
}

/// <summary>The shortcut choices for showing / hiding the overlay (RegisterHotKey modifiers and virtual keys).</summary>
public static class OverlayHotkeys
{
    public const string None = "none";

    private const uint Alt = 1;
    private const uint Ctrl = 2;
    private const uint Shift = 4;

    public static IReadOnlyList<string> All { get; } = ["ctrl+alt+f", "ctrl+shift+f12", "alt+f12", "ctrl+alt+o", None];

    public static string Normalize(string? id) => All.Contains(id ?? "") ? id! : All[0];

    /// <summary>Modifiers and key for RegisterHotKey, or null for "none".</summary>
    public static (uint Modifiers, uint Key)? Keys(string? id) => Normalize(id) switch
    {
        "ctrl+alt+f" => (Ctrl | Alt, 0x46u),
        "ctrl+shift+f12" => (Ctrl | Shift, 0x7Bu),
        "alt+f12" => (Alt, 0x7Bu),
        "ctrl+alt+o" => (Ctrl | Alt, 0x4Fu),
        _ => null,
    };

    /// <summary>"Ctrl+Alt+F", "" for none.</summary>
    public static string Display(string? id)
    {
        var n = Normalize(id);
        return n == None
            ? ""
            : string.Join("+", n.Split('+').Select(p => p.Length <= 3 && (p.Length == 1 || p[0] == 'f') ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}

public static class OverlayPositions
{
    public const string TopLeft = "top-left";
    public const string TopRight = "top-right";
    public const string BottomLeft = "bottom-left";
    public const string BottomRight = "bottom-right";

    public static IReadOnlyList<string> All { get; } = [TopLeft, TopRight, BottomLeft, BottomRight];

    public static string Normalize(string? id) => All.Contains(id ?? "") ? id! : TopLeft;

    /// <summary>Top-left point of a window of the given size in a corner of the monitor (physical pixels).</summary>
    public static (int X, int Y) Place(string? position, ScreenRect monitor, int width, int height, int margin)
    {
        var p = Normalize(position);
        var right = p.EndsWith("right", StringComparison.Ordinal);
        var bottom = p.StartsWith("bottom", StringComparison.Ordinal);
        return (right ? monitor.Right - margin - width : monitor.Left + margin,
                bottom ? monitor.Bottom - margin - height : monitor.Top + margin);
    }
}
