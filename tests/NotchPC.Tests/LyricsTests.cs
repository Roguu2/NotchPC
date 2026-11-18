using System.Net;
using System.Text;
using NotchPC.Core.Lyrics;

namespace NotchPC.Tests;

public sealed class LrcParserTests
{
    [Fact]
    public void Parses_stamps_and_sorts_lines()
    {
        var lines = LrcParser.Parse("[ar:Queen]\n[00:07.13] Caught in a landslide\r\n[00:00.15] Is this the real life?\n");

        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(150), lines[0].Time);
        Assert.Equal("Is this the real life?", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(7130), lines[1].Time);
    }

    [Fact]
    public void Repeated_stamps_produce_one_line_each()
    {
        var lines = LrcParser.Parse("[00:10.00][01:20.50]Chorus");

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(80.5)], lines.Select(l => l.Time));
        Assert.All(lines, l => Assert.Equal("Chorus", l.Text));
    }

    [Theory]
    [InlineData("[01:02]x", 62_000)]
    [InlineData("[01:02.5]x", 62_500)]
    [InlineData("[01:02.123]x", 62_123)]
    [InlineData("[01:02:45]x", 62_450)]
    public void Accepts_timestamp_variants(string lrc, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), LrcParser.Parse(lrc).Single().Time);
    }

    [Fact]
    public void Positive_offset_makes_lyrics_come_sooner()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:10.00]a\n[00:00.20]b");

        Assert.Equal(TimeSpan.Zero, lines[0].Time); // clamped, never negative
        Assert.Equal(TimeSpan.FromSeconds(9.5), lines[1].Time);
    }

    [Fact]
    public void Empty_text_lines_are_kept_as_pauses()
    {
        var lines = LrcParser.Parse("[00:01.00]a\n[00:05.00]\n[00:09.00]b");

        Assert.Equal(3, lines.Count);
        Assert.Equal("", lines[1].Text);
    }

    [Fact]
    public void Garbage_gives_no_lines()
    {
        Assert.Empty(LrcParser.Parse("just some text\nno stamps"));
        Assert.Empty(LrcParser.Parse(null));
    }

    [Fact]
    public void Line_index_follows_position()
    {
        var doc = new LyricsDocument(LyricsKind.Synced, LrcParser.Parse("[00:01.00]a\n[00:05.00]b\n[00:09.00]c"), "");

        Assert.Equal(-1, doc.LineIndexAt(TimeSpan.FromSeconds(0.5)));
        Assert.Equal(0, doc.LineIndexAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, doc.LineIndexAt(TimeSpan.FromSeconds(8.99)));
        Assert.Equal(2, doc.LineIndexAt(TimeSpan.FromMinutes(10)));
    }
}

public sealed class TrackQueryTests
{
    [Theory]
    [InlineData("Song (Official Video)", "Song")]
    [InlineData("Song [Official Music Video]", "Song")]
    [InlineData("Song (Lyrics)", "Song")]
    [InlineData("Song (Remastered 2011)", "Song")]
    [InlineData("Song (feat. Someone)", "Song (feat. Someone)")]
    [InlineData("  Song   (Live)  ", "Song (Live)")]
    public void Cleans_titles(string raw, string expected)
    {
        Assert.Equal(expected, TrackQueryCleaner.CleanTitle(raw));
    }

    [Theory]
    [InlineData("Queen - Topic", "Queen")]
    [InlineData("QueenVEVO", "Queen")]
    [InlineData("Queen", "Queen")]
    public void Cleans_artists(string raw, string expected)
    {
        Assert.Equal(expected, TrackQueryCleaner.CleanArtist(raw));
    }

    [Fact]
    public void Youtube_style_title_adds_split_candidate()
    {
        var c = TrackQuery.CandidatesFor("Some Channel", "Queen - Bohemian Rhapsody (Official Video)", "", TimeSpan.Zero);

        Assert.Equal(2, c.Count);
        Assert.Equal(("Some Channel", "Queen - Bohemian Rhapsody"), (c[0].Artist, c[0].Title));
        Assert.Equal(("Queen", "Bohemian Rhapsody"), (c[1].Artist, c[1].Title));
    }

    [Fact]
    public void Missing_artist_uses_only_split_candidate()
    {
        var c = TrackQuery.CandidatesFor("", "Queen - Bohemian Rhapsody", "", TimeSpan.Zero);

        Assert.Equal(("Queen", "Bohemian Rhapsody"), (c.Single().Artist, c.Single().Title));
    }
}

public sealed class LrclibClientTests : IDisposable
{
    private const string Found = """
        {"trackName":"Song","artistName":"Artist","albumName":"Album","duration":200.0,"instrumental":false,
         "plainLyrics":"a\nb","syncedLyrics":"[00:01.00]a\n[00:02.00]b"}
        """;

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "NotchPC.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public async Task Get_hit_returns_synced_lyrics_and_is_cached()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, Found));
        var client = new LrclibClient(new HttpClient(handler), _cache);

        var first = await client.GetAsync("Artist", "Song", "Album", TimeSpan.FromSeconds(200), CancellationToken.None);
        var second = await client.GetAsync("Artist", "Song", "Album", TimeSpan.FromSeconds(200), CancellationToken.None);

        Assert.Equal(LyricsKind.Synced, first.Kind);
        Assert.Equal(2, first.Lines.Count);
        Assert.Equal(LyricsKind.Synced, second.Kind);
        Assert.Single(handler.Requests);
        Assert.Contains("api/get?artist_name=Artist&track_name=Song&album_name=Album&duration=200", handler.Requests[0]);
    }

    [Fact]
    public async Task Get_404_falls_back_to_search_and_filters_by_duration()
    {
        var handler = new StubHandler(uri => uri.Contains("/api/get", StringComparison.Ordinal)
            ? (HttpStatusCode.NotFound, "{}")
            : (HttpStatusCode.OK, """
                [{"trackName":"Song","artistName":"Artist","duration":260.0,"syncedLyrics":"[00:01.00]wrong edit"},
                 {"trackName":"Song","artistName":"Artist","duration":201.0,"plainLyrics":"right one"}]
                """));
        var client = new LrclibClient(new HttpClient(handler), _cache);

        var doc = await client.GetAsync("Artist", "Song", "", TimeSpan.FromSeconds(200), CancellationToken.None);

        Assert.Equal(LyricsKind.Plain, doc.Kind);
        Assert.Equal("right one", doc.PlainText);
    }

    [Fact]
    public async Task Not_found_is_cached_but_server_errors_are_not()
    {
        var handler = new StubHandler(uri => uri.Contains("/api/search", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, "[]")
            : (HttpStatusCode.NotFound, "{}"));
        var client = new LrclibClient(new HttpClient(handler), _cache);

        Assert.Equal(LyricsKind.NotFound, (await client.GetAsync("A", "S", "", TimeSpan.FromSeconds(100), CancellationToken.None)).Kind);
        var calls = handler.Requests.Count;
        Assert.Equal(LyricsKind.NotFound, (await client.GetAsync("A", "S", "", TimeSpan.FromSeconds(100), CancellationToken.None)).Kind);
        Assert.Equal(calls, handler.Requests.Count);

        var failing = new LrclibClient(new HttpClient(new StubHandler(_ => (HttpStatusCode.InternalServerError, ""))), _cache);
        await Assert.ThrowsAsync<LyricsUnavailableException>(() =>
            failing.GetAsync("B", "T", "", TimeSpan.FromSeconds(100), CancellationToken.None));
        // One cache file from the not-found lookup; the failed (500) lookup must not add another.
        Assert.Single(Directory.GetFiles(_cache));
    }

    [Fact]
    public async Task Not_found_cache_expires()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new StubHandler(uri => uri.Contains("/api/search", StringComparison.Ordinal) ? (HttpStatusCode.OK, "[]") : (HttpStatusCode.NotFound, "{}"));
        var client = new LrclibClient(new HttpClient(handler), _cache, () => now);

        await client.GetAsync("A", "S", "", TimeSpan.FromSeconds(100), CancellationToken.None);
        var calls = handler.Requests.Count;
        now = now.AddDays(4);
        await client.GetAsync("A", "S", "", TimeSpan.FromSeconds(100), CancellationToken.None);

        Assert.True(handler.Requests.Count > calls);
    }

    [Fact]
    public void Instrumental_record_maps_to_instrumental()
    {
        Assert.Equal(LyricsKind.Instrumental, new LrclibRecord { Instrumental = true }.ToDocument().Kind);
    }

    private sealed class StubHandler(Func<string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Requests.Add(uri);
            var (status, body) = respond(uri);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
