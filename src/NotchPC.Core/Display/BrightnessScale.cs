namespace NotchPC.Core.Display;

/// <summary>Converts between a monitor's own DDC/CI brightness range (often 0..100, sometimes other) and percent.</summary>
public static class BrightnessScale
{
    /// <summary>Percent 0..100, or null when the range is unusable (max ≤ min).</summary>
    public static int? ToPercent(uint min, uint current, uint max)
    {
        if (max <= min)
        {
            return null;
        }

        var clamped = Math.Clamp(current, min, max);
        return (int)Math.Round((clamped - min) * 100.0 / (max - min), MidpointRounding.AwayFromZero);
    }

    /// <summary>The monitor value for a percent (clamped to 0..100).</summary>
    public static uint FromPercent(int percent, uint min, uint max)
    {
        if (max <= min)
        {
            return min;
        }

        var p = Math.Clamp(percent, 0, 100);
        return (uint)Math.Round(min + ((max - min) * p / 100.0), MidpointRounding.AwayFromZero);
    }
}
