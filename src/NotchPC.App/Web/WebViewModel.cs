using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Layout;
using NotchPC.Core.Settings;
using NotchPC.Core.Web;
using NotchPC.Platform.Monitors;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Web;

/// <summary>
/// The web tab of the notch (address box + tiles) and the panel window it opens. The window (and its WebView2,
/// ~100–150 MB) is created on first use and closed a minute after it was hidden.
/// </summary>
internal sealed class WebViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(1);
    private const double GapBelowNotch = 50; // DIPs from the top of the monitor

    private readonly SettingsService _settings;
    private readonly DispatcherTimer _idle;
    private WebPanelWindow? _window;

    public WebViewModel(SettingsService settings)
    {
        _settings = settings;
        if (_settings.Current.Web.Bookmarks is null)
        {
            var defaults = DefaultBookmarks();
            _settings.Update(s => s.Web.Bookmarks = defaults);
        }

        _idle = new DispatcherTimer { Interval = IdleClose };
        _idle.Tick += (_, _) => CloseWindow();
        _settings.Changed += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>Something is about to open: the notch should collapse out of the way.</summary>
    public event EventHandler? Opening;

    public ObservableCollection<WebTile> Tiles { get; } = [];

    public static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotchPC", "WebView2");

    public void OpenText(string text)
    {
        if (WebAddress.Resolve(text) is { } uri)
        {
            Open(uri);
        }
    }

    public void Open(WebTile tile)
    {
        if (Uri.TryCreate(tile.Url, UriKind.Absolute, out var uri) && WebAddress.IsWeb(tile.Url))
        {
            Open(uri);
        }
    }

    public void Remove(WebTile tile) =>
        _settings.Update(s => s.Web.Bookmarks?.RemoveAll(b => b.Url == tile.Url && b.Name == tile.Name));

    public void Dispose()
    {
        _idle.Stop();
        CloseWindow();
    }

    private static List<WebBookmark> DefaultBookmarks() =>
    [
        new() { Name = "YouTube", Url = "https://www.youtube.com/" },
        new() { Name = "Google", Url = "https://www.google.com/" },
        new() { Name = "Tłumacz", Url = "https://translate.google.com/" },
        new() { Name = "ChatGPT", Url = "https://chatgpt.com/" },
        new() { Name = "Wikipedia", Url = "https://pl.wikipedia.org/" },
    ];

    private void Open(Uri uri)
    {
        Opening?.Invoke(this, EventArgs.Empty);
        _idle.Stop();
        if (_window is null)
        {
            _window = new WebPanelWindow(DataFolder);
            _window.BookmarkRequested += (_, b) => AddBookmark(b);
            _window.IsVisibleChanged += (_, _) => OnWindowVisibility();
        }

        _window.HideOnDeactivate = _settings.Current.Web.HideOnDeactivate;
        Place(_window);
        _window.Show();
        _window.Activate();
        _ = _window.NavigateAsync(uri);
    }

    /// <summary>Centred under the notch on the notch's monitor, in physical pixels (DPI of that monitor).</summary>
    private void Place(Window window)
    {
        var monitors = MonitorService.GetMonitors();
        if (NotchLayout.SelectMonitor(monitors, _settings.Current.Notch.Monitor) is not { } m)
        {
            return;
        }

        var web = _settings.Current.Web;
        var work = m.WorkArea;
        var width = Math.Min((int)(web.Width * m.Scale), work.Width - 16);
        var height = Math.Min((int)(web.Height * m.Scale), work.Height - (int)(GapBelowNotch * m.Scale) - 8);
        var x = m.Bounds.Left + ((m.Bounds.Width - width) / 2);
        var y = m.Bounds.Top + (int)((_settings.Current.Notch.TopOffset + GapBelowNotch) * m.Scale);
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        WindowHelper.Place(hwnd, ScreenRect.FromSize(x, y, width, height));
    }

    private void OnWindowVisibility()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible)
        {
            _idle.Stop();
            return;
        }

        // Remember a size the user dragged.
        var w = _window.ActualWidth;
        var h = _window.ActualHeight;
        if (w > 0 && h > 0 && (Math.Abs(w - _settings.Current.Web.Width) > 2 || Math.Abs(h - _settings.Current.Web.Height) > 2))
        {
            _settings.Update(s =>
            {
                s.Web.Width = Math.Round(w);
                s.Web.Height = Math.Round(h);
            });
        }

        _idle.Start();
    }

    private void CloseWindow()
    {
        _idle.Stop();
        if (_window is { } window)
        {
            _window = null;
            window.AllowClose = true;
            window.Close();
            AppLog.Info("Web panel closed (idle)");
        }
    }

    private void AddBookmark(WebBookmarkRequest request)
    {
        if (_settings.Current.Web.Bookmarks?.Any(b => b.Url == request.Url) == true)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(request.Title) ? WebAddress.ShortName(request.Url) : request.Title.Trim();
        if (name.Length > 24)
        {
            name = WebAddress.ShortName(request.Url);
        }

        _settings.Update(s => (s.Web.Bookmarks ??= []).Add(new WebBookmark { Name = name, Url = request.Url }));
    }

    private void Rebuild()
    {
        var list = _settings.Current.Web.Bookmarks ?? [];
        if (Tiles.Select(t => (t.Name, t.Url)).SequenceEqual(list.Select(b => (b.Name, b.Url))))
        {
            return;
        }

        Tiles.Clear();
        foreach (var b in list)
        {
            Tiles.Add(new WebTile(b.Name, b.Url));
        }
    }
}

/// <summary>A bookmark tile: a coloured letter (no favicons: fetching them would tell a third party what you use).</summary>
internal sealed record WebTile(string Name, string Url)
{
    private static readonly System.Windows.Media.Color[] Palette =
    [
        System.Windows.Media.Color.FromRgb(0xFF, 0x45, 0x3A), System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x0A), System.Windows.Media.Color.FromRgb(0x30, 0xD1, 0x58), System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF),
        System.Windows.Media.Color.FromRgb(0xBF, 0x5A, 0xF2), System.Windows.Media.Color.FromRgb(0xFF, 0x37, 0x5F), System.Windows.Media.Color.FromRgb(0x64, 0xD2, 0xFF), System.Windows.Media.Color.FromRgb(0xAC, 0x8E, 0x68),
    ];

    public string Letter => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    public Brush Fill
    {
        get
        {
            var hash = 0;
            foreach (var c in WebAddress.ShortName(Url))
            {
                hash = (hash * 31) + c;
            }

            var brush = new SolidColorBrush(Palette[Math.Abs(hash % Palette.Length)]);
            brush.Freeze();
            return brush;
        }
    }
}
