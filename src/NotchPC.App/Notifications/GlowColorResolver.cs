using System.Globalization;
using System.Windows.Media;
using NotchPC.Core.Notifications;
using Windows.UI.ViewManagement;

namespace NotchPC.App.Notifications;

/// <summary>Turns the glow colour setting into a colour ("accent" = the current Windows accent colour).</summary>
internal static class GlowColorResolver
{
    private static readonly Color Fallback = Color.FromRgb(0x5A, 0xC8, 0xFA);

    public static Color Resolve(string setting)
    {
        if (!string.Equals(setting, GlowColors.Accent, StringComparison.OrdinalIgnoreCase))
        {
            return Parse(setting) ?? Fallback;
        }

        try
        {
            // The light variant: the plain accent colour is often too dark to glow on a black island.
            var c = new UISettings().GetColorValue(UIColorType.AccentLight1);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return Fallback;
        }
    }

    public static Color? Parse(string hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return null;
    }
}
