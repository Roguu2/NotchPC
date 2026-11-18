namespace NotchPC.Core.Lyrics;

public sealed record LyricsLine(TimeSpan Time, string Text);

public enum LyricsKind
{
    /// <summary>Time-stamped lines, highlighted as the song plays.</summary>
    Synced,

    /// <summary>Text without timing.</summary>
    Plain,

    /// <summary>The track has no vocals (LRCLIB "instrumental").</summary>
    Instrumental,

    NotFound,
}

/// <summary>Lyrics for one track, ready to display.</summary>
public sealed class LyricsDocument
{
    public static readonly LyricsDocument NotFound = new(LyricsKind.NotFound, [], "");
    public static readonly LyricsDocument Instrumental = new(LyricsKind.Instrumental, [], "");

    public LyricsDocument(LyricsKind kind, IReadOnlyList<LyricsLine> lines, string plainText)
    {
        Kind = kind;
        Lines = lines;
        PlainText = plainText;
    }

    public LyricsKind Kind { get; }

    /// <summary>Sorted by time (synced lyrics only).</summary>
    public IReadOnlyList<LyricsLine> Lines { get; }

    public string PlainText { get; }

    /// <summary>Index of the line being sung at <paramref name="position"/>, or -1 before the first line.</summary>
    public int LineIndexAt(TimeSpan position)
    {
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }
}
