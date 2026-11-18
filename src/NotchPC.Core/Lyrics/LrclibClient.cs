using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotchPC.Core.Lyrics;

/// <summary>One record of the LRCLIB API (https://lrclib.net/docs).</summary>
public sealed record LrclibRecord
{
    [JsonPropertyName("trackName")]
    public string? TrackName { get; init; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; init; }

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; init; }

    /// <summary>Seconds.</summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; init; }

    [JsonPropertyName("instrumental")]
    public bool Instrumental { get; init; }

    [JsonPropertyName("plainLyrics")]
    public string? PlainLyrics { get; init; }

    [JsonPropertyName("syncedLyrics")]
    public string? SyncedLyrics { get; init; }

    public LyricsDocument ToDocument()
    {
        if (!string.IsNullOrWhiteSpace(SyncedLyrics))
        {
            var lines = LrcParser.Parse(SyncedLyrics);
            if (lines.Count > 0)
            {
                return new LyricsDocument(LyricsKind.Synced, lines, PlainLyrics ?? "");
            }
        }

        if (!string.IsNullOrWhiteSpace(PlainLyrics))
        {
            return new LyricsDocument(LyricsKind.Plain, [], PlainLyrics.Trim());
        }

        return Instrumental ? LyricsDocument.Instrumental : LyricsDocument.NotFound;
    }
}

/// <summary>The lyrics service could not be reached (network down, timeout, server error). Not cached.</summary>
public sealed class LyricsUnavailableException : Exception
{
    public LyricsUnavailableException()
    {
    }

    public LyricsUnavailableException(string message)
        : base(message)
    {
    }

    public LyricsUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Looks up lyrics on LRCLIB (free, no account). Results are cached on disk: hits forever, misses for a few days.
/// Only called when the lyrics card is open, never in the background.
/// </summary>
public sealed class LrclibClient
{
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(3);
    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly Func<DateTimeOffset> _now;

    public LrclibClient(HttpClient http, string cacheDirectory, Func<DateTimeOffset>? clock = null)
    {
        _http = http;
        _cacheDir = cacheDirectory;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static Uri BaseAddress { get; } = new("https://lrclib.net/");

    /// <summary>LRCLIB asks clients to identify themselves; no personal data is sent.</summary>
    public static string UserAgent => $"NotchPC/{typeof(LrclibClient).Assembly.GetName().Version?.ToString(3) ?? "0"}";

    /// <exception cref="LyricsUnavailableException">Network or server failure.</exception>
    public async Task<LyricsDocument> GetAsync(string artist, string title, string album, TimeSpan duration, CancellationToken ct)
    {
        var candidates = TrackQuery.CandidatesFor(artist, title, album, duration);
        if (candidates.Count == 0)
        {
            return LyricsDocument.NotFound;
        }

        foreach (var q in candidates)
        {
            if (TryReadCache(q) is { } cached)
            {
                if (cached.Kind != LyricsKind.NotFound)
                {
                    return cached;
                }

                continue;
            }

            var record = await FetchAsync(q, ct).ConfigureAwait(false);
            var doc = record?.ToDocument() ?? LyricsDocument.NotFound;
            WriteCache(q, record);
            if (doc.Kind != LyricsKind.NotFound)
            {
                return doc;
            }
        }

        return LyricsDocument.NotFound;
    }

    /// <summary>Best search hit for the query (synced lyrics and matching duration preferred), or null.</summary>
    public static LrclibRecord? PickBest(IEnumerable<LrclibRecord> records, TrackQuery query)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(query);
        LrclibRecord? best = null;
        var bestScore = int.MinValue;

        foreach (var r in records)
        {
            var hasContent = !string.IsNullOrWhiteSpace(r.SyncedLyrics) || !string.IsNullOrWhiteSpace(r.PlainLyrics) || r.Instrumental;
            if (!hasContent)
            {
                continue;
            }

            var score = 0;
            if (query.Duration > TimeSpan.Zero && r.Duration is { } d)
            {
                var diff = Math.Abs(d - query.Duration.TotalSeconds);
                if (diff > 5)
                {
                    continue; // another version/edit: timings would be wrong
                }

                score += diff <= 2 ? 3 : 1;
            }

            if (!string.IsNullOrWhiteSpace(r.SyncedLyrics))
            {
                score += 4;
            }

            if (string.Equals(r.ArtistName?.Trim(), query.Artist, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (string.Equals(r.TrackName?.Trim(), query.Title, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }

        return best;
    }

    private async Task<LrclibRecord?> FetchAsync(TrackQuery q, CancellationToken ct)
    {
        try
        {
            if (q.Duration > TimeSpan.Zero)
            {
                var getUrl = "api/get?artist_name=" + Uri.EscapeDataString(q.Artist) +
                             "&track_name=" + Uri.EscapeDataString(q.Title) +
                             "&album_name=" + Uri.EscapeDataString(q.Album) +
                             "&duration=" + ((int)Math.Round(q.Duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                using var response = await _http.GetAsync(new Uri(BaseAddress, getUrl), ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<LrclibRecord>(ct).ConfigureAwait(false);
                }

                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    throw new LyricsUnavailableException($"LRCLIB returned {(int)response.StatusCode}");
                }
            }

            var searchUrl = "api/search?artist_name=" + Uri.EscapeDataString(q.Artist) +
                            "&track_name=" + Uri.EscapeDataString(q.Title);
            using var search = await _http.GetAsync(new Uri(BaseAddress, searchUrl), ct).ConfigureAwait(false);
            if (!search.IsSuccessStatusCode)
            {
                throw new LyricsUnavailableException($"LRCLIB search returned {(int)search.StatusCode}");
            }

            var list = await search.Content.ReadFromJsonAsync<List<LrclibRecord>>(ct).ConfigureAwait(false) ?? [];
            return PickBest(list, q);
        }
        catch (HttpRequestException ex)
        {
            throw new LyricsUnavailableException("LRCLIB is unreachable", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LyricsUnavailableException("LRCLIB timed out", ex);
        }
        catch (JsonException ex)
        {
            throw new LyricsUnavailableException("LRCLIB sent an unexpected response", ex);
        }
    }

    private string CachePath(TrackQuery q)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(q.CacheKey)))[..32];
        return Path.Combine(_cacheDir, hash + ".json");
    }

    private LyricsDocument? TryReadCache(TrackQuery q)
    {
        var path = CachePath(q);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path), CacheJson);
            if (entry is null)
            {
                return null;
            }

            if (entry.Record is null)
            {
                return _now() - entry.FetchedAt < NotFoundTtl ? LyricsDocument.NotFound : null;
            }

            return entry.Record.ToDocument();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteCache(TrackQuery q, LrclibRecord? record)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(CachePath(q), JsonSerializer.Serialize(new CacheEntry(_now(), record), CacheJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache is an optimisation only.
        }
    }

    private sealed record CacheEntry(DateTimeOffset FetchedAt, LrclibRecord? Record);
}
