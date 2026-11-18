using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Clipboard;
using NotchPC.Core.Localization;

namespace NotchPC.App.Clipboard;

/// <summary>Clipboard tab of the notch. The list is rebuilt only while the tab is on screen.</summary>
internal sealed class ClipboardViewModel : ObservableObject
{
    private readonly ClipboardService _service;
    private string _query = "";
    private bool _visible;
    private bool _dirty = true;

    public ClipboardViewModel(ClipboardService service)
    {
        _service = service;
        _service.Changed += (_, _) =>
        {
            _dirty = true;
            if (_visible)
            {
                Refresh();
            }
        };
        Loc.CultureChanged += (_, _) => _dirty = true;
    }

    public ObservableCollection<ClipItemViewModel> Items { get; } = [];

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? ""))
            {
                Refresh();
            }
        }
    }

    public bool Enabled => _service.Enabled;

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Message shown when the list is empty.</summary>
    public string EmptyText => !_service.Enabled ? Loc.Get("Clip.Disabled")
        : _query.Length > 0 ? Loc.Get("Clip.NoResults")
        : Loc.Get("Clip.Empty");

    public string EmptyHint => _service.Enabled && _query.Length == 0 ? Loc.Get("Clip.EmptyHint") : "";

    public ClipboardService Service => _service;

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible && _query.Length > 0)
        {
            // Next time the panel opens with the full list.
            _query = "";
            Raise(nameof(Query));
            _dirty = true;
        }

        if (visible && _dirty)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        _dirty = false;
        var now = DateTimeOffset.Now;
        var thumbs = Items.Where(i => i.HasThumbnailLoaded).ToDictionary(i => i.Entry.Id, i => i);
        Items.Clear();
        foreach (var e in _service.View(_query))
        {
            var item = new ClipItemViewModel(e, now, _service);
            if (thumbs.TryGetValue(e.Id, out var old))
            {
                item.ReuseThumbnail(old);
            }

            Items.Add(item);
        }

        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyText));
        Raise(nameof(EmptyHint));
        Raise(nameof(Enabled));
    }
}

/// <summary>One row of the clipboard list.</summary>
internal sealed class ClipItemViewModel
{
    private const int MaxTitle = 160;
    private readonly ClipboardService _service;
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public ClipItemViewModel(ClipEntry entry, DateTimeOffset now, ClipboardService service)
    {
        Entry = entry;
        _service = service;
        var time = TimeText(entry.At, now);
        switch (entry.Kind)
        {
            case ClipKind.Text:
                var lines = (entry.Text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2).ToList();
                Title = Shorten(lines.FirstOrDefault() ?? "");
                Subtitle = lines.Count > 1 ? $"{time} · {Shorten(lines[1])}" : time;
                Glyph = LooksLikeLink(entry.Text) ? "" : ""; // Link / Document
                break;
            case ClipKind.Image:
                Title = Loc.Format("Clip.Image", entry.ImageWidth, entry.ImageHeight);
                Subtitle = time;
                Glyph = ""; // Photo
                break;
            default:
                var files = entry.Files ?? [];
                Title = files.Count == 1 ? Path.GetFileName(files[0]) : Loc.Format("Clip.Files", files.Count);
                Subtitle = files.Count == 1 ? $"{time} · {Path.GetDirectoryName(files[0])}" : $"{time} · {string.Join(", ", files.Take(3).Select(Path.GetFileName))}";
                Glyph = files.Count == 1 && Directory.Exists(files[0]) ? "" : ""; // Folder / Page
                break;
        }
    }

    public ClipEntry Entry { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Glyph { get; }

    public bool IsImage => Entry.Kind == ClipKind.Image;

    public bool Pinned => Entry.Pinned;

    public string PinGlyph => Entry.Pinned ? "" : ""; // Unpin / Pin

    public string PinTip => Loc.Get(Entry.Pinned ? "Clip.Unpin" : "Clip.Pin");

    public bool HasThumbnailLoaded => _thumbnailLoaded;

    /// <summary>Small preview decoded on first use (only rows on screen are created by the virtualizing list).</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded && IsImage)
            {
                _thumbnailLoaded = true;
                _thumbnail = Decode(_service.LoadImage(Entry.Id));
            }

            return _thumbnail;
        }
    }

    public void ReuseThumbnail(ClipItemViewModel other)
    {
        _thumbnail = other._thumbnail;
        _thumbnailLoaded = true;
    }

    internal static string TimeText(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1))
        {
            return Loc.Get("Clip.JustNow");
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Loc.Format("Clip.MinutesAgo", (int)age.TotalMinutes);
        }

        var local = at.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("d MMM, HH:mm", Loc.Culture);
    }

    private static bool LooksLikeLink(string? text) =>
        text is not null && !text.Contains('\n', StringComparison.Ordinal) &&
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string Shorten(string s)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > MaxTitle ? s[..MaxTitle] + "…" : s;
    }

    private static BitmapImage? Decode(byte[]? png)
    {
        if (png is null)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(png);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelHeight = 80;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
