namespace NotchPC.Core.Music;

/// <summary>Picks an accent colour from album art for the panel glow (like the tinted player in the reference).</summary>
public static class CoverColor
{
    /// <summary>
    /// Saturation-weighted average of BGRA32 pixels, so a colourful cover on a grey background still gives
    /// its colour rather than mud. Returns (r, g, b) or null for an empty/fully transparent image.
    /// </summary>
    public static (byte R, byte G, byte B)? Accent(ReadOnlySpan<byte> bgra)
    {
        double r = 0, g = 0, b = 0, weightSum = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128)
            {
                continue;
            }

            double pb = bgra[i], pg = bgra[i + 1], pr = bgra[i + 2];
            var max = Math.Max(pr, Math.Max(pg, pb));
            var min = Math.Min(pr, Math.Min(pg, pb));
            var saturation = max <= 0 ? 0 : (max - min) / max;
            var brightness = max / 255.0;
            // Prefer saturated, not-too-dark pixels; keep a small base weight so greyscale covers still work.
            var w = 0.05 + (saturation * saturation * brightness);
            r += pr * w;
            g += pg * w;
            b += pb * w;
            weightSum += w;
        }

        if (weightSum <= 0)
        {
            return null;
        }

        return ((byte)Math.Round(r / weightSum), (byte)Math.Round(g / weightSum), (byte)Math.Round(b / weightSum));
    }
}
