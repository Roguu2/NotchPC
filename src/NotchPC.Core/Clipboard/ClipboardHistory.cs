using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NotchPC.Core.Clipboard;

public enum ClipKind
{
    Text,
    Image,
    Files,
}

/// <summary>One remembered clipboard content.</summary>
public sealed record ClipEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public ClipKind Kind { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>PNG kept next to the history (encrypted), for images.</summary>
    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    public DateTimeOffset At { get; init; }

    public bool Pinned { get; init; }

    /// <summary>Content fingerprint: copying the same thing again moves the entry to the top instead of duplicating it.</summary>
    public string Hash { get; init; } = "";

    public static string HashOf(ClipKind kind, string? text, IReadOnlyList<string>? files, ReadOnlySpan<byte> image)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData([(byte)kind]);
        if (text is not null)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(text));
        }

        foreach (var f in files ?? [])
        {
            sha.AppendData(Encoding.UTF8.GetBytes(f + "\n"));
        }

        sha.AppendData(image);
        return Convert.ToHexString(sha.GetHashAndReset())[..32];
    }

    /// <summary>Text used for searching and as the one-line preview.</summary>
    public string SearchText => Kind switch
    {
        ClipKind.Text => Text ?? "",
        ClipKind.Files => string.Join(" ", (Files ?? []).Select(Path.GetFileName)),
        _ => "",
    };
}

/// <summary>
/// Clipboard history: newest first, de-duplicated, limited (pinned entries are never dropped).
/// Pure logic; persistence is in <see cref="ClipboardStore"/>.
/// </summary>
public sealed class ClipboardHistory
{
    private readonly List<ClipEntry> _items = [];

    public ClipboardHistory(int limit = 100)
    {
        Limit = Math.Max(1, limit);
    }

    public int Limit { get; set; }

    public IReadOnlyList<ClipEntry> Items => _items;

    public void Load(IEnumerable<ClipEntry> entries)
    {
        _items.Clear();
        _items.AddRange(entries.OrderByDescending(e => e.At));
    }

    /// <summary>
    /// Adds an entry at the top. The same content already in the list is moved up (keeping its pin).
    /// Returns entries that fell out of the limit (so their image files can be deleted).
    /// </summary>
    public IReadOnlyList<ClipEntry> Add(ClipEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var existing = _items.FindIndex(e => e.Hash == entry.Hash);
        if (existing >= 0)
        {
            var old = _items[existing];
            _items.RemoveAt(existing);
            _items.Insert(0, old with { At = entry.At });
            return [];
        }

        _items.Insert(0, entry);
        return Trim();
    }

    public void SetPinned(Guid id, bool pinned)
    {
        var i = _items.FindIndex(e => e.Id == id);
        if (i >= 0)
        {
            _items[i] = _items[i] with { Pinned = pinned };
        }
    }

    public ClipEntry? Remove(Guid id)
    {
        var i = _items.FindIndex(e => e.Id == id);
        if (i < 0)
        {
            return null;
        }

        var e = _items[i];
        _items.RemoveAt(i);
        return e;
    }

    /// <summary>Removes everything that is not pinned; returns what was removed.</summary>
    public IReadOnlyList<ClipEntry> ClearUnpinned()
    {
        var removed = _items.Where(e => !e.Pinned).ToList();
        _items.RemoveAll(e => !e.Pinned);
        return removed;
    }

    /// <summary>Pinned first, then newest; filtered by a case-insensitive substring (empty = everything).</summary>
    public IReadOnlyList<ClipEntry> View(string? query)
    {
        var q = query?.Trim() ?? "";
        return _items
            .Where(e => q.Length == 0 || e.SearchText.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(e => e.Pinned)
            .ThenByDescending(e => e.At)
            .ToList();
    }

    private List<ClipEntry> Trim()
    {
        var removed = new List<ClipEntry>();
        while (_items.Count > Limit)
        {
            var oldest = _items.FindLastIndex(e => !e.Pinned);
            if (oldest < 0)
            {
                break; // everything pinned: allowed to exceed the limit
            }

            removed.Add(_items[oldest]);
            _items.RemoveAt(oldest);
        }

        return removed;
    }
}

/// <summary>
/// Saves the history encrypted: one file for the list, one per image. The protect/unprotect functions are
/// DPAPI (current Windows user) in the app; tests pass simple transforms.
/// </summary>
public sealed class ClipboardStore(string directory, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private string IndexPath => Path.Combine(directory, "history.dat");

    public IReadOnlyList<ClipEntry> LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return [];
            }

            var bytes = unprotect(File.ReadAllBytes(IndexPath));
            return JsonSerializer.Deserialize<List<ClipEntry>>(bytes, Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or InvalidOperationException or UnauthorizedAccessException)
        {
            return []; // unreadable (other user/PC, corrupt): start fresh
        }
    }

    public void SaveIndex(IEnumerable<ClipEntry> entries)
    {
        Directory.CreateDirectory(directory);
        var tmp = IndexPath + ".tmp";
        File.WriteAllBytes(tmp, protect(JsonSerializer.SerializeToUtf8Bytes(entries.ToList(), Json)));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    public void SaveImage(Guid id, byte[] png)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(ImagePath(id), protect(png));
    }

    public byte[]? LoadImage(Guid id)
    {
        try
        {
            var path = ImagePath(id);
            return File.Exists(path) ? unprotect(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void DeleteImage(Guid id)
    {
        try
        {
            File.Delete(ImagePath(id));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Deletes everything (history and images).</summary>
    public void DeleteAll()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string ImagePath(Guid id) => Path.Combine(directory, id.ToString("N") + ".img");
}
