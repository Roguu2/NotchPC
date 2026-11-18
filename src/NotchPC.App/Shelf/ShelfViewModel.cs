using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;

namespace NotchPC.App.Shelf;

/// <summary>
/// File shelf: files dropped on the notch wait here until they are dragged somewhere else.
/// Only the paths are remembered (settings.json); the files themselves are never copied or moved.
/// </summary>
internal sealed class ShelfViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public ShelfViewModel(SettingsService settings)
    {
        _settings = settings;
        foreach (var path in settings.Current.Shelf.Files)
        {
            Items.Add(new ShelfItem(path));
        }

        Loc.CultureChanged += (_, _) => Raise(nameof(CountText));
    }

    public ObservableCollection<ShelfItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public string CountText => Loc.Format("Shelf.Count", Items.Count);

    /// <summary>True while the user drags files out of the shelf (so the notch does not take them back).</summary>
    public bool IsDraggingOut { get; set; }

    /// <summary>Adds paths that are not on the shelf yet. Returns how many were added.</summary>
    public int Add(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var item = new ShelfItem(path);
            Items.Add(item);
            item.Refresh();
            added++;
        }

        if (added > 0)
        {
            Save();
        }

        return added;
    }

    public void Remove(ShelfItem item)
    {
        if (Items.Remove(item))
        {
            Save();
        }
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    /// <summary>Checks the files again when the tab is opened (they may have been moved or deleted meanwhile).</summary>
    public void SetVisible(bool visible)
    {
        if (visible)
        {
            foreach (var item in Items)
            {
                item.Refresh();
            }
        }
    }

    public static void Open(ShelfItem item)
    {
        if (!item.Exists)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Opening a shelf file failed", ex);
        }
    }

    public static void ShowInFolder(ShelfItem item)
    {
        try
        {
            var args = item.Exists ? $"/select,\"{item.Path}\"" : $"\"{Path.GetDirectoryName(item.Path)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Show in folder failed", ex);
        }
    }

    private void Save()
    {
        var paths = Items.Select(i => i.Path).ToList();
        _settings.Update(s => s.Shelf.Files = paths);
        Raise(nameof(IsEmpty));
        Raise(nameof(CountText));
    }
}

/// <summary>One file or folder on the shelf.</summary>
internal sealed class ShelfItem : ObservableObject
{
    private bool _exists = true;
    private ImageSource? _thumbnail;
    private bool _loading;

    public ShelfItem(string path)
    {
        Path = path;
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        Name = string.IsNullOrEmpty(name) ? path : name;
    }

    public string Path { get; }

    public string Name { get; }

    public bool Exists
    {
        get => _exists;
        private set
        {
            if (Set(ref _exists, value))
            {
                Raise(nameof(ToolTip));
            }
        }
    }

    public string ToolTip => Exists ? Path : $"{Path}\n{Loc.Get("Shelf.Missing")}";

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_loading)
            {
                _ = LoadThumbnailAsync();
            }

            return _thumbnail;
        }
    }

    public void Refresh() => Exists = File.Exists(Path) || Directory.Exists(Path);

    private async Task LoadThumbnailAsync()
    {
        _loading = true;
        _thumbnail = await ShellImageLoader.LoadAsync(Path, 96).ConfigureAwait(true);
        Raise(nameof(Thumbnail));
    }
}
