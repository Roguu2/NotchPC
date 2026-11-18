using System.Globalization;
using System.Text.RegularExpressions;

namespace NotchPC.Core.Lyrics;

/// <summary>
/// Parses LRC lyrics: "[mm:ss.xx]text", several stamps per line ("[00:10.00][01:20.00]chorus"),
/// "[mm:ss]" and "[mm:ss:xx]" variants, and the [offset:±ms] tag. Metadata tags ([ar:], [ti:] …) are skipped.
/// </summary>
public static partial class LrcParser
{
    public static IReadOnlyList<LyricsLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return [];
        }

        var offset = TimeSpan.Zero;
        var lines = new List<LyricsLine>();

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var offsetMatch = OffsetTag().Match(line);
            if (offsetMatch.Success)
            {
                // Positive offset = lyrics come sooner.
                offset = TimeSpan.FromMilliseconds(int.Parse(offsetMatch.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
                continue;
            }

            var stamps = new List<TimeSpan>();
            var rest = line;
            Match m;
            while ((m = LeadingStamp().Match(rest)).Success)
            {
                stamps.Add(ToTime(m));
                rest = rest[m.Length..];
            }

            if (stamps.Count == 0)
            {
                continue; // metadata or garbage
            }

            var text = rest.Trim();
            foreach (var s in stamps)
            {
                lines.Add(new LyricsLine(s, text));
            }
        }

        return lines
            .Select(l => l with { Time = l.Time - offset < TimeSpan.Zero ? TimeSpan.Zero : l.Time - offset })
            .OrderBy(l => l.Time)
            .ToList();
    }

    private static TimeSpan ToTime(Match m)
    {
        var minutes = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var fraction = m.Groups["f"].Success ? m.Groups["f"].Value : "";
        var ms = fraction.Length switch
        {
            0 => 0,
            1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
            2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
            _ => int.Parse(fraction[..3], CultureInfo.InvariantCulture),
        };
        return new TimeSpan(0, 0, minutes, seconds, ms);
    }

    [GeneratedRegex(@"^\s*\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]")]
    private static partial Regex LeadingStamp();

    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();
}
