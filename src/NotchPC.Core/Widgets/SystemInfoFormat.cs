using System.Globalization;
using System.Text.RegularExpressions;

namespace NotchPC.Core.Widgets;

/// <summary>Texts of the "Disks and system" widget (pure).</summary>
public static partial class SystemInfoFormat
{
    /// <summary>"931 GB", "1,8 TB", "512 MB" (decimal units, like Windows Explorer shows disk sizes… almost: Explorer
    /// uses binary units with decimal names; this follows Explorer so the numbers match).</summary>
    public static string Size(long bytes, CultureInfo culture)
    {
        const double k = 1024;
        double b = Math.Max(0, bytes);
        return b switch
        {
            >= k * k * k * k => (b / (k * k * k * k)).ToString("0.0", culture) + " TB",
            >= k * k * k => (b / (k * k * k)).ToString("0", culture) + " GB",
            >= k * k => (b / (k * k)).ToString("0", culture) + " MB",
            _ => (b / k).ToString("0", culture) + " KB",
        };
    }

    /// <summary>"3 d 4 h", "5 h 12 min", "8 min".</summary>
    public static string Uptime(TimeSpan t)
    {
        if (t.TotalDays >= 1)
        {
            return $"{(int)t.TotalDays} d {t.Hours} h";
        }

        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(0, t.Minutes)} min";
    }

    /// <summary>"Microsoft Windows 10.0.26300" → "Windows 11 · 26300" (build 22000+ is Windows 11).</summary>
    public static string Windows(string osDescription)
    {
        var m = Build().Match(osDescription ?? "");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var build))
        {
            return osDescription ?? "";
        }

        return $"Windows {(build >= 22000 ? 11 : 10)} · {build}";
    }

    /// <summary>Used fraction 0–1 of a drive.</summary>
    public static double Used(long total, long free) => total <= 0 ? 0 : Math.Clamp((double)(total - free) / total, 0, 1);

    [GeneratedRegex(@"10\.0\.(\d{4,6})")]
    private static partial Regex Build();
}
