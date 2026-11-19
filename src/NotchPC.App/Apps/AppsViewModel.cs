using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Core.Settings;

namespace NotchPC.App.Apps;

/// <summary>App shortcuts desktop widget (also listed on the phone).</summary>
internal sealed class AppsViewModel : NotchPC.App.Widgets.WidgetViewModel
{
    private readonly SettingsService _settings;

    public AppsViewModel(SettingsService settings)
    {
        _settings = settings;
        EnsureDefaults(settings);
        Rebuild();
    }

    /// <summary>App tiles followed by the "Add" tile.</summary>
    public ObservableCollection<object> Tiles { get; } = [];

    /// <summary>First start: a few useful shortcuts.</summary>
    public static void EnsureDefaults(SettingsService settings)
    {
        if (settings.Current.Apps.Shortcuts is null)
        {
            var defaults = DefaultShortcuts().ToList();
            settings.Update(s => s.Apps.Shortcuts = defaults);
        }
    }

    public void Add(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var name = DisplayName(target);
        _settings.Update(s => (s.Apps.Shortcuts ??= []).Add(new AppShortcut { Name = name, Target = target }));
        Rebuild();
    }

    public void Remove(AppTile tile)
    {
        _settings.Update(s => s.Apps.Shortcuts?.RemoveAll(a => ReferenceEquals(a, tile.Shortcut)));
        Rebuild();
    }

    /// <summary>A shortcut dragged onto another (also the order on the phone).</summary>
    public void Move(AppTile dragged, AppTile target, bool after)
    {
        _settings.Update(s => s.Apps.Shortcuts = NotchPC.Core.Layout.ListOrder.Move(
            s.Apps.Shortcuts ?? [], dragged.Shortcut, target.Shortcut, after, ReferenceEqualityComparer.Instance as IEqualityComparer<AppShortcut>));
        Rebuild();
    }

    public static bool Launch(AppTile tile)
    {
        try
        {
            Process.Start(new ProcessStartInfo(tile.Shortcut.Target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Error("Starting a shortcut failed", ex);
            return false;
        }
    }

    /// <summary>Name for a new shortcut: the program's description ("Paint"), else the file name.</summary>
    internal static string DisplayName(string target)
    {
        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            var description = FileVersionInfo.GetVersionInfo(target).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && description.Length <= 24)
            {
                return description;
            }
        }

        var name = Path.GetFileNameWithoutExtension(target);
        return string.IsNullOrEmpty(name) ? target : name;
    }

    private static IEnumerable<AppShortcut> DefaultShortcuts()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.SystemDirectory;
        (string Key, string Path)[] candidates =
        [
            ("Apps.Default.Notepad", System.IO.Path.Combine(system, "notepad.exe")),
            ("Apps.Default.Explorer", System.IO.Path.Combine(windows, "explorer.exe")),
            ("Apps.Default.Paint", System.IO.Path.Combine(system, "mspaint.exe")),
            ("Apps.Default.Calculator", System.IO.Path.Combine(system, "calc.exe")),
        ];
        foreach (var (key, path) in candidates.Where(c => File.Exists(c.Path)))
        {
            yield return new AppShortcut { Name = Loc.Get(key), Target = path };
        }

        yield return new AppShortcut { Name = Loc.Get("Apps.Default.Settings"), Target = "ms-settings:" };
    }

    protected override void OnScreenChanged(bool onScreen)
    {
    }

    private void Rebuild()
    {
        Tiles.Clear();
        foreach (var s in _settings.Current.Apps.Shortcuts ?? [])
        {
            Tiles.Add(new AppTile(s));
        }

        Tiles.Add(AddTile.Instance);
    }
}

/// <summary>One app shortcut.</summary>
internal sealed class AppTile(AppShortcut shortcut) : ObservableObject
{
    private ImageSource? _icon;
    private bool _loading;

    public AppShortcut Shortcut { get; } = shortcut;

    public string Name => Shortcut.Name;

    public string ToolTip => Shortcut.Target;

    /// <summary>Shown while there is no icon (URIs such as ms-settings:, missing files).</summary>
    public string Glyph => Shortcut.Target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? "" : "";

    public ImageSource? Icon
    {
        get
        {
            if (_icon is null && !_loading)
            {
                _loading = true;
                _ = LoadAsync();
            }

            return _icon;
        }
    }

    private async Task LoadAsync()
    {
        if (Shortcut.Target.Contains(':', StringComparison.Ordinal) && !Path.IsPathRooted(Shortcut.Target))
        {
            return; // URI: no file to take an icon from
        }

        _icon = await ShellImageLoader.LoadAsync(Shortcut.Target, 64, iconOnly: true).ConfigureAwait(true);
        Raise(nameof(Icon));
        Raise(nameof(HasIcon));
    }

    public bool HasIcon => _icon is not null;
}

/// <summary>Marker item for the "Add" tile at the end of the row.</summary>
internal sealed class AddTile
{
    public static AddTile Instance { get; } = new();
}
