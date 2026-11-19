using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using NotchPC.App.Infrastructure;
using NotchPC.App.Settings;
using NotchPC.Core.Localization;
using NotchPC.Core.Settings;
using NotchPC.Core.Widgets;
using NotchPC.Platform.Security;

namespace NotchPC.App.Widgets;

/// <summary>Saves a small piece of state as DPAPI-encrypted JSON in %AppData%\NotchPC (debounced).</summary>
internal sealed class EncryptedStore<T>(string fileName)
    where T : class, new()
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path = Path.Combine(SettingsStore.DefaultDirectory, fileName);

    public T Load()
    {
        try
        {
            return File.Exists(_path) && Dpapi.TryUnprotect(File.ReadAllText(_path)) is { } json
                ? JsonSerializer.Deserialize<T>(json, Json) ?? new T()
                : new T();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error($"Reading {fileName} failed", ex);
            return new T();
        }
    }

    public void Save(T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, Dpapi.Protect(JsonSerializer.Serialize(value, Json)));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Saving {fileName} failed", ex);
        }
    }
}

public sealed class StickyNote
{
    public string Text { get; set; } = "";

    public string Color { get; set; } = "#FFD60A";
}

public sealed class StickyData
{
    public List<StickyNote> Notes { get; set; } = [new StickyNote()];

    public int Current { get; set; }
}

/// <summary>Sticky notes: coloured pages you flip through, each saving itself (encrypted).</summary>
internal sealed class StickyNotesViewModel : WidgetViewModel
{
    private static readonly string[] Colors = ["#FFD60A", "#30D158", "#5AC8FA", "#FF375F", "#BF5AF2", "#8E8E93"];

    private readonly EncryptedStore<StickyData> _store = new("sticky.dat");
    private readonly DispatcherTimer _save;
    private readonly StickyData _data;

    public StickyNotesViewModel()
    {
        _data = _store.Load();
        if (_data.Notes.Count == 0)
        {
            _data.Notes.Add(new StickyNote());
        }

        _data.Current = Math.Clamp(_data.Current, 0, _data.Notes.Count - 1);
        _save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            _store.Save(_data);
        };
    }

    private StickyNote Note => _data.Notes[_data.Current];

    public string Text
    {
        get => Note.Text;
        set
        {
            if (value == Note.Text)
            {
                return;
            }

            Note.Text = value ?? "";
            Changed(nameof(Text), nameof(IsEmpty));
        }
    }

    public bool IsEmpty => Note.Text.Length == 0;

    public Brush Tint => Brush(Note.Color, 0x2E);

    public Brush Dot => Brush(Note.Color, 0xFF);

    public string PageText => $"{_data.Current + 1}/{_data.Notes.Count}";

    public bool HasMany => _data.Notes.Count > 1;

    public void Next(int delta)
    {
        _data.Current = (_data.Current + delta + _data.Notes.Count) % _data.Notes.Count;
        Changed();
    }

    public void Add()
    {
        _data.Notes.Insert(_data.Current + 1, new StickyNote { Color = Colors[(_data.Notes.Count) % Colors.Length] });
        _data.Current++;
        Changed();
    }

    public void Delete()
    {
        if (_data.Notes.Count == 1)
        {
            Note.Text = "";
        }
        else
        {
            _data.Notes.RemoveAt(_data.Current);
            _data.Current = Math.Min(_data.Current, _data.Notes.Count - 1);
        }

        Changed();
    }

    public void CycleColor()
    {
        var i = Array.IndexOf(Colors, Note.Color);
        Note.Color = Colors[(i + 1) % Colors.Length];
        Changed(nameof(Tint), nameof(Dot));
    }

    /// <summary>
    /// The quick-note tab was removed: its text (note.dat) becomes a sticky note, the sticky notes widget is turned on
    /// so the text is visible, and the old file goes away. Runs once at start.
    /// </summary>
    public static void ImportOldNote(SettingsService settings)
    {
        var old = Path.Combine(SettingsStore.DefaultDirectory, "note.dat");
        try
        {
            if (!File.Exists(old))
            {
                return;
            }

            var text = Dpapi.TryUnprotect(File.ReadAllText(old)) ?? "";
            if (text.Trim().Length > 0)
            {
                var store = new EncryptedStore<StickyData>("sticky.dat");
                var data = store.Load();
                if (data.Notes.Count == 1 && data.Notes[0].Text.Length == 0)
                {
                    data.Notes[0].Text = text;
                }
                else
                {
                    data.Notes.Add(new StickyNote { Text = text, Color = "#5AC8FA" });
                    data.Current = data.Notes.Count - 1;
                }

                store.Save(data);
                if (!File.Exists(Path.Combine(SettingsStore.DefaultDirectory, "sticky.dat")))
                {
                    return; // not saved: keep the old file
                }

                settings.Update(s => s.Widgets.StickyEnabled = true);
                AppLog.Info("Quick note moved to a sticky note");
            }

            File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Moving the quick note failed", ex);
        }
    }

    public override void Dispose()
    {
        if (_save.IsEnabled)
        {
            _save.Stop();
            _store.Save(_data);
        }

        base.Dispose();
    }

    protected override void OnScreenChanged(bool onScreen)
    {
    }

    private static SolidColorBrush Brush(string hex, byte alpha)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private void Changed(params string[] names)
    {
        _save.Stop();
        _save.Start();
        RaiseMany(names.Length > 0 ? names : [nameof(Text), nameof(IsEmpty), nameof(Tint), nameof(Dot), nameof(PageText), nameof(HasMany)]);
    }
}

public sealed class TodoEntry
{
    public string Text { get; set; } = "";

    public bool Done { get; set; }
}

public sealed class TodoData
{
    public List<TodoEntry> Items { get; set; } = [];
}

/// <summary>One row of the to-do list.</summary>
internal sealed class TodoItem(TodoEntry entry, Action changed) : ObservableObject
{
    public TodoEntry Entry { get; } = entry;

    public string Text => Entry.Text;

    public bool Done
    {
        get => Entry.Done;
        set
        {
            if (value == Entry.Done)
            {
                return;
            }

            Entry.Done = value;
            Raise(nameof(Done));
            changed();
        }
    }
}

/// <summary>To-do list: add with Enter, tick off, remove; saves itself (encrypted).</summary>
internal sealed class TodoViewModel : WidgetViewModel
{
    private readonly EncryptedStore<TodoData> _store = new("todo.dat");
    private readonly TodoData _data;
    private string _newText = "";

    public TodoViewModel()
    {
        _data = _store.Load();
        foreach (var e in _data.Items)
        {
            Items.Add(new TodoItem(e, Save));
        }
    }

    public ObservableCollection<TodoItem> Items { get; } = [];

    public string NewText
    {
        get => _newText;
        set => Set(ref _newText, value ?? "");
    }

    public string CountText => Loc.Format("Widget.Todo.Count", Items.Count(i => !i.Done), Items.Count);

    public bool HasDone => Items.Any(i => i.Done);

    public bool IsEmpty => Items.Count == 0;

    public void Add()
    {
        var text = _newText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var entry = new TodoEntry { Text = text };
        _data.Items.Add(entry);
        Items.Add(new TodoItem(entry, Save));
        NewText = "";
        Save();
    }

    public void Remove(TodoItem item)
    {
        _data.Items.Remove(item.Entry);
        Items.Remove(item);
        Save();
    }

    public void ClearDone()
    {
        foreach (var item in Items.Where(i => i.Done).ToList())
        {
            Remove(item);
        }
    }

    protected override void OnScreenChanged(bool onScreen)
    {
    }

    protected override void OnCulture() => RaiseMany(nameof(CountText));

    private void Save()
    {
        _store.Save(_data);
        RaiseMany(nameof(CountText), nameof(HasDone), nameof(IsEmpty));
    }
}

/// <summary>One drive in the system widget (Used in percent, for the bar).</summary>
internal sealed record DriveRow(string Name, string Detail, double Used, bool Warning);

/// <summary>Disks and system: drive usage, how long Windows has been on, the Windows version. Updated while on screen.</summary>
internal sealed class SystemViewModel : WidgetViewModel
{
    private readonly DispatcherTimer _refresh;

    public SystemViewModel()
    {
        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _refresh.Tick += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<DriveRow> Drives { get; } = [];

    public string UptimeText { get; private set; } = "";

    public string WindowsText { get; } = SystemInfoFormat.Windows(RuntimeInformation.OSDescription);

    public string MachineText { get; } = Environment.MachineName;

    public override void Dispose()
    {
        _refresh.Stop();
        base.Dispose();
    }

    protected override void OnScreenChanged(bool onScreen)
    {
        if (onScreen)
        {
            Refresh();
            _refresh.Start();
        }
        else
        {
            _refresh.Stop();
        }
    }

    protected override void OnCulture() => Refresh();

    private void Refresh()
    {
        var culture = Loc.Culture;
        var rows = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                var used = SystemInfoFormat.Used(d.TotalSize, d.TotalFreeSpace);
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name.TrimEnd('\\') : $"{d.Name.TrimEnd('\\')} {d.VolumeLabel}";
                var detail = Loc.Format("Widget.System.Free", SystemInfoFormat.Size(d.TotalFreeSpace, culture), SystemInfoFormat.Size(d.TotalSize, culture));
                rows.Add(new DriveRow(label, detail, used * 100, used >= 0.9));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive went away meanwhile.
            }
        }

        if (!Drives.SequenceEqual(rows))
        {
            Drives.Clear();
            foreach (var r in rows)
            {
                Drives.Add(r);
            }
        }

        UptimeText = Loc.Format("Widget.System.Uptime", SystemInfoFormat.Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        RaiseMany(nameof(UptimeText));
    }
}
