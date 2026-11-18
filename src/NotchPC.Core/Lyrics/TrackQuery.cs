using System.Text.RegularExpressions;

namespace NotchPC.Core.Lyrics;

/// <summary>Artist + title as sent to the lyrics service.</summary>
public sealed record TrackQuery(string Artist, string Title, string Album, TimeSpan Duration)
{
    /// <summary>
    /// Cleaned queries to try, best first. Browser sessions (YouTube) often report the channel as artist
    /// and "Artist - Title (Official Video)" as title, so a split variant is added.
    /// </summary>
    public static IReadOnlyList<TrackQuery> CandidatesFor(string artist, string title, string album, TimeSpan duration)
    {
        var cleanTitle = TrackQueryCleaner.CleanTitle(title);
        var cleanArtist = TrackQueryCleaner.CleanArtist(artist);
        var result = new List<TrackQuery>();

        if (cleanTitle.Length > 0 && cleanArtist.Length > 0)
        {
            result.Add(new TrackQuery(cleanArtist, cleanTitle, album.Trim(), duration));
        }

        var dash = cleanTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            var left = cleanTitle[..dash].Trim();
            var right = TrackQueryCleaner.CleanTitle(cleanTitle[(dash + 3)..]);
            if (left.Length > 0 && right.Length > 0)
            {
                result.Add(new TrackQuery(left, right, "", duration));
            }
        }

        return result.Distinct().ToList();
    }

    /// <summary>Stable cache key (case-insensitive, duration rounded to whole seconds).</summary>
    public string CacheKey =>
        $"{Artist.ToUpperInvariant()}|{Title.ToUpperInvariant()}|{(int)Math.Round(Duration.TotalSeconds)}";
}

public static partial class TrackQueryCleaner
{
    public static string CleanTitle(string title)
    {
        var t = (title ?? "").Trim();
        t = NoiseInBrackets().Replace(t, "");
        t = MultiSpace().Replace(t, " ").Trim();
        return t.TrimEnd('-', '|', ' ');
    }

    public static string CleanArtist(string artist)
    {
        var a = (artist ?? "").Trim();
        a = TopicSuffix().Replace(a, "");
        a = VevoSuffix().Replace(a, "");
        return a.Trim();
    }

    // "(Official Video)", "[Official Music Video]", "(Lyrics)", "(Audio)", "(HD)", "(Visualizer)", "(Remastered 2011)" …
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|hd|hq|4k|remaster(ed)?|mv|clip|teledysk)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseInBrackets();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"\s*-\s*Topic$", RegexOptions.IgnoreCase)]
    private static partial Regex TopicSuffix();

    [GeneratedRegex(@"VEVO$", RegexOptions.IgnoreCase)]
    private static partial Regex VevoSuffix();
}
