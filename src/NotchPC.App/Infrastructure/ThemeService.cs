using System.Windows;
using System.Windows.Media;
using NotchPC.Core.Settings;

namespace NotchPC.App.Infrastructure;

/// <summary>
/// Paints the shared brushes of the notch and the widgets (NotchStyles.xaml) with the chosen theme. The brushes are
/// changed in place, so every window that uses them follows at once, without a restart.
/// </summary>
internal static class ThemeService
{
    private static string? _applied;

    public static void Apply(ThemeSettings theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var accent = ThemePalette.ParseAccent(theme.Accent) ?? WindowsAccent();
        var signature = $"{theme.Style}|{theme.Accent}|{theme.Opacity}|{theme.Corners}|{accent:X6}";
        if (signature == _applied || Application.Current is not { } app)
        {
            return;
        }

        _applied = signature;
        foreach (var (key, argb) in ThemePalette.For(theme, accent))
        {
            var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            if (app.TryFindResource(key) is SolidColorBrush { IsFrozen: false } brush)
            {
                brush.Color = color;
            }
            else
            {
                app.Resources[key] = new SolidColorBrush(color); // only reached by brushes looked up later
            }
        }
    }

    private static uint WindowsAccent()
    {
        var c = Notifications.GlowColorResolver.Resolve(Core.Notifications.GlowColors.Accent);
        return ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }
}
