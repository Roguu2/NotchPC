using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Games;
using NotchPC.Core.Localization;

namespace NotchPC.App.Games;

/// <summary>One game in the tab: its cover (Steam) or exe icon, name and how long it was played this week.</summary>
internal sealed class GameTile : ObservableObject
{
    private ImageSource? _cover;
    private ImageSource? _icon;
    private string _subtitle = "";
    private bool _favorite;
    private bool _hidden;

    public GameTile(InstalledGame game) => Game = game;

    public InstalledGame Game { get; private set; }

    public string Name => Game.Name;

    public string Subtitle
    {
        get => _subtitle;
        set => Set(ref _subtitle, value);
    }

    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            Raise(nameof(Cover));
            Raise(nameof(ShowIcon));
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Raise(nameof(Icon));
            Raise(nameof(ShowIcon));
        }
    }

    public bool ShowIcon => _cover is null && _icon is not null;

    public bool ImageRequested { get; set; }

    public bool IsFavorite
    {
        get => _favorite;
        set => Set(ref _favorite, value);
    }

    /// <summary>Hidden from the list (shown dimmed only while "show hidden" is on).</summary>
    public bool IsHidden
    {
        get => _hidden;
        set => Set(ref _hidden, value);
    }

    public void Replace(InstalledGame game) => Game = game;
}

/// <summary>
/// The games desktop widget: installed games (Steam, Epic, GOG, Riot, Battle.net …) to start with one click, play time this
/// week and the last session (time, average and low FPS). Favourites stay on top, games can be hidden, three sort orders.
/// </summary>
internal sealed class GamesViewModel : NotchPC.App.Widgets.WidgetViewModel
{
    private readonly GameLibraryService _library;
    private readonly GameHistoryStore _history;
    private readonly SettingsService _settings;
    private readonly Dictionary<string, GameTile> _tiles = [];
    private IReadOnlyList<InstalledGame> _all = [];
    private bool _loading;
    private bool _showHidden;

    public GamesViewModel(GameLibraryService library, GameHistoryStore history, SettingsService settings)
    {
        _library = library;
        _history = history;
        _settings = settings;
        _history.Changed += OnHistoryChanged;
        _settings.Changed += OnSettingsChanged;
        RefreshTexts();
        _ = RefreshAsync();
    }

    /// <summary>Hidden games appear (dimmed, with a restore button) while this is on.</summary>
    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (Set(ref _showHidden, value))
            {
                Arrange();
            }
        }
    }

    public bool HasHidden => W.GameHidden.Count > 0;

    public string SortText => Loc.Get("Games.Sort." + GameSortModes.Normalize(W.GameSort));

    private Core.Settings.WidgetSettings W => _settings.Current.Widgets;

    public ObservableCollection<GameTile> Games { get; } = [];

    public string WeekText { get; private set; } = "";

    public string LastTitle { get; private set; } = "";

    public string LastDetail { get; private set; } = "";

    public bool HasLast { get; private set; }

    public bool IsLoading
    {
        get => _loading;
        private set
        {
            Set(ref _loading, value);
            Raise(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => !_loading && Games.Count == 0;

    /// <summary>The tab opened: re-read the libraries (cached for a moment) and update the list.</summary>
    public async Task RefreshAsync(bool fresh = false)
    {
        IsLoading = Games.Count == 0;
        var games = await _library.GetAsync(fresh).ConfigureAwait(true);
        IsLoading = false;

        _all = games;
        Arrange();
        foreach (var tile in Games.Where(t => !t.ImageRequested))
        {
            tile.ImageRequested = true;
            _ = LoadImageAsync(tile);
        }
    }

    public void ToggleFavorite(GameTile tile) =>
        _settings.Update(x => x.Widgets.GameFavorites = GameOrder.Toggle(x.Widgets.GameFavorites, GameOrder.Key(tile.Game)));

    public void ToggleHidden(GameTile tile) =>
        _settings.Update(x => x.Widgets.GameHidden = GameOrder.Toggle(x.Widgets.GameHidden, GameOrder.Key(tile.Game)));

    /// <summary>A tile dragged onto another: the list switches to the user's own order.</summary>
    public void Move(GameTile dragged, GameTile target, bool after)
    {
        var shown = Games.Select(t => GameOrder.Key(t.Game)).ToList();
        var moved = NotchPC.Core.Layout.ListOrder.Move(shown, GameOrder.Key(dragged.Game), GameOrder.Key(target.Game), after, StringComparer.OrdinalIgnoreCase);
        _settings.Update(x =>
        {
            // Games not on screen now (hidden) keep their place after the visible ones.
            x.Widgets.GameOrder = moved.Concat(x.Widgets.GameOrder.Where(k => !moved.Contains(k, StringComparer.OrdinalIgnoreCase))).ToList();
            x.Widgets.GameSort = GameSortModes.Custom;
        });
    }

    public void CycleSort() => _settings.Update(x => x.Widgets.GameSort = GameSortModes.Next(x.Widgets.GameSort));

    public static void Launch(GameTile tile)
    {
        var g = tile.Game;
        try
        {
            Process.Start(new ProcessStartInfo(g.Launch, g.LaunchArgs ?? "")
            {
                UseShellExecute = true,
                WorkingDirectory = g.WorkingDir ?? "",
            })?.Dispose();
            AppLog.Info($"Starting game {g.Name} ({g.Source})");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Error($"Starting {g.Name} failed", ex);
        }
    }

    public override void Dispose()
    {
        _history.Changed -= OnHistoryChanged;
        _settings.Changed -= OnSettingsChanged;
        base.Dispose();
    }

    protected override void OnScreenChanged(bool onScreen)
    {
        if (onScreen)
        {
            _ = RefreshAsync(); // new installs, play time
        }
    }

    protected override void OnCulture() => RefreshTexts();

    private void OnHistoryChanged(object? sender, EventArgs e) => Arrange();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (W.GameHidden.Count == 0 && _showHidden)
        {
            _showHidden = false; // nothing left to restore
            Raise(nameof(ShowHidden));
        }

        Arrange();
    }

    /// <summary>Puts the tiles in order (favourites, sort mode, hidden) without reading the libraries again.</summary>
    private void Arrange()
    {
        var w = W;
        var ordered = GameOrder.Arrange(_all, _history.Sessions, w.GameSort, w.GameFavorites, w.GameHidden, _showHidden, w.GameOrder);
        var favorites = new HashSet<string>(w.GameFavorites, StringComparer.OrdinalIgnoreCase);
        var hidden = new HashSet<string>(w.GameHidden, StringComparer.OrdinalIgnoreCase);
        var keys = ordered.Select(GameOrder.Key).ToList();
        if (!keys.SequenceEqual(Games.Select(t => GameOrder.Key(t.Game))))
        {
            Games.Clear();
            foreach (var g in ordered)
            {
                if (!_tiles.TryGetValue(GameOrder.Key(g), out var tile))
                {
                    _tiles[GameOrder.Key(g)] = tile = new GameTile(g);
                }
                else
                {
                    tile.Replace(g);
                }

                Games.Add(tile);
            }
        }

        foreach (var tile in Games)
        {
            var key = GameOrder.Key(tile.Game);
            tile.IsFavorite = favorites.Contains(key);
            tile.IsHidden = hidden.Contains(key);
        }

        Raise(nameof(IsEmpty));
        Raise(nameof(HasHidden));
        Raise(nameof(SortText));
        RefreshTexts();
        foreach (var tile in Games.Where(t => !t.ImageRequested))
        {
            tile.ImageRequested = true;
            _ = LoadImageAsync(tile);
        }
    }

    /// <summary>"avg 87 FPS · low 61", or null without readings.</summary>
    private static string? FpsText(GameSession s)
    {
        var culture = Loc.Culture;
        return GameSessions.Fps(s.AvgFps, culture) is { } avg
            ? Loc.Format("Games.Fps", avg, GameSessions.Fps(s.LowFps, culture) ?? "—")
            : null;
    }

    private static async Task LoadImageAsync(GameTile tile)
    {
        if (tile.Game.Image is not { } path)
        {
            return;
        }

        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            tile.Icon = await ShellImageLoader.LoadAsync(path, 128, iconOnly: true).ConfigureAwait(true);
            return;
        }

        var (image, small) = await Task.Run(() => LoadPicture(path)).ConfigureAwait(true);
        if (small)
        {
            tile.Icon = image;
        }
        else
        {
            tile.Cover = image;
        }
    }

    /// <summary>A cover (decoded small enough for the tile) or, for tiny pictures, an icon.</summary>
    private static (ImageSource? Image, bool Small) LoadPicture(string path)
    {
        try
        {
            // An .ico holds several sizes: take the biggest (sharp icon and a smooth blurred backdrop).
            var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames
                .OrderByDescending(f => f.PixelWidth).First();
            if (frame.PixelWidth <= 64 || path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                frame.Freeze();
                return (frame, true);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 300;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return (image, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            return (null, false);
        }
    }

    private void RefreshTexts()
    {
        var history = _history.Sessions;
        var now = DateTimeOffset.Now;
        var weekStart = GameSessions.WeekStart(now);
        var week = GameSessions.MinutesSince(history, weekStart);
        WeekText = week >= 1 ? Loc.Format("Games.Week", GameSessions.Duration(week)) : Loc.Get("Games.Week.None");

        if (history.Count > 0)
        {
            var last = history[^1];
            var when = last.Start.Date == now.Date ? last.Start.ToString("t", Loc.Culture) : last.Start.ToString("d MMM", Loc.Culture);
            LastTitle = $"{last.Game} · {when}";
            LastDetail = FpsText(last) is { } fps ? $"{GameSessions.Duration(last.Minutes)} · {fps}" : GameSessions.Duration(last.Minutes);
            HasLast = true;
        }

        foreach (var tile in Games)
        {
            var minutes = GameSessions.MinutesSince(history, weekStart, tile.Name);
            tile.Subtitle = minutes >= 1 ? Loc.Format("Games.Game.Week", GameSessions.Duration(minutes)) : GameSources.DisplayName(tile.Game.Source);
        }

        Raise(nameof(WeekText));
        Raise(nameof(LastTitle));
        Raise(nameof(LastDetail));
        Raise(nameof(HasLast));
    }
}

/// <summary>Text of the notch flash after a game: "Cyberpunk 2077 · 2 h 14 min · 87 FPS".</summary>
internal static class GameSessionText
{
    public static string Flash(GameSession s) =>
        GameSessions.Fps(s.AvgFps, Loc.Culture) is { } fps
            ? Loc.Format("Games.Flash.Fps", s.Game, GameSessions.Duration(s.Minutes), fps)
            : Loc.Format("Games.Flash", s.Game, GameSessions.Duration(s.Minutes));
}
