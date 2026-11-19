namespace NotchPC.Core.Settings;

/// <summary>Look of the notch and the widgets (stage 14).</summary>
public sealed class ThemeSettings
{
    /// <summary>dark (default), oled (pure black) or glass (see-through).</summary>
    public string Style { get; set; } = ThemeStyles.Dark;

    /// <summary>"accent" (Windows accent colour), "white" or #RRGGBB: sliders, the selected tab, highlights.</summary>
    public string Accent { get; set; } = "white";

    /// <summary>How opaque the notch and the widgets are, 60–100 %.</summary>
    public int Opacity { get; set; } = 95;

    /// <summary>round (pill) or soft (smaller corners).</summary>
    public string Corners { get; set; } = ThemeCorners.Round;
}

public static class ThemeStyles
{
    public const string Dark = "dark";
    public const string Oled = "oled";
    public const string Glass = "glass";

    public static IReadOnlyList<string> All { get; } = [Dark, Oled, Glass];

    public static string Normalize(string? s) => All.Contains(s ?? "") ? s! : Dark;
}

public static class ThemeCorners
{
    public const string Round = "round";
    public const string Soft = "soft";

    public static IReadOnlyList<string> All { get; } = [Round, Soft];

    public static string Normalize(string? s) => All.Contains(s ?? "") ? s! : Round;

    /// <summary>Corner radii (closed pill, open panel, notification banner) in DIPs.</summary>
    public static (double Collapsed, double Expanded, double Banner) Radii(string corners) =>
        Normalize(corners) == Soft ? (9, 16, 14) : (16, 28, 26);
}

public static class ThemeAccents
{
    public const string Windows = "accent";
    public const string White = "white";

    public static IReadOnlyList<string> All { get; } = [White, Windows, "#5AC8FA", "#BF5AF2", "#FF375F", "#FF9F0A", "#30D158"];

    public static string Normalize(string? s) =>
        All.FirstOrDefault(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase)) ?? White;
}

/// <summary>
/// The colours of a theme as ARGB values, by resource key (the brushes in NotchStyles.xaml). Pure: the app paints the
/// brushes with them, so everything that uses them changes at once, without a restart.
/// </summary>
public static class ThemePalette
{
    /// <param name="accentRgb">The accent colour already resolved (Windows accent → its RGB).</param>
    public static IReadOnlyDictionary<string, uint> For(ThemeSettings theme, uint accentRgb)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var opacity = Math.Clamp(theme.Opacity, 60, 100) / 100.0;
        var style = ThemeStyles.Normalize(theme.Style);
        var (islandRgb, widgetRgb, opacityScale, border) = style switch
        {
            ThemeStyles.Oled => (0x000000u, 0x000000u, 1.0, 0x14u),
            ThemeStyles.Glass => (0x1C1C22u, 0x1C1C22u, 0.72, 0x40u),
            _ => (0x0B0B0Du, 0x141416u, 1.0, 0x1Fu),
        };

        var island = Alpha(opacity * opacityScale) | islandRgb;
        var widget = Alpha(opacity * opacityScale * 0.95) | widgetRgb;
        return new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["IslandBackground"] = island,
            ["IslandBorder"] = (border << 24) | 0xFFFFFFu,
            ["WidgetBackground"] = widget,
            ["WidgetBorder"] = ((border + 7) << 24) | 0xFFFFFFu,
            ["NotchAccent"] = 0xFF000000u | (accentRgb & 0xFFFFFFu),
            ["CalendarAccentBrush"] = 0xFF000000u | (accentRgb == 0xFFFFFFu ? 0x4C8DFFu : accentRgb & 0xFFFFFFu),
        };
    }

    private static uint Alpha(double fraction) => (uint)Math.Round(Math.Clamp(fraction, 0, 1) * 255) << 24;

    /// <summary>"#5AC8FA" → 0x5AC8FA; "white" → 0xFFFFFF; null for "accent" (the app asks Windows).</summary>
    public static uint? ParseAccent(string accent)
    {
        var a = ThemeAccents.Normalize(accent);
        if (a == ThemeAccents.White)
        {
            return 0xFFFFFFu;
        }

        if (a == ThemeAccents.Windows)
        {
            return null;
        }

        return uint.TryParse(a.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb) ? rgb : 0xFFFFFFu;
    }
}
