namespace NotchPC.Core.Games;

/// <summary>How the games widget orders the list (favourites always first).</summary>
public static class GameSortModes
{
    public const string Recent = "recent";
    public const string Name = "name";
    public const string Playtime = "playtime";

    /// <summary>The user's own order (set by dragging the tiles).</summary>
    public const string Custom = "custom";

    public static IReadOnlyList<string> All { get; } = [Recent, Name, Playtime, Custom];

    /// <summary>The next mode for the sort button (an unknown mode counts as recent).</summary>
    public static string Next(string? mode)
    {
        var i = All.ToList().IndexOf(Normalize(mode));
        return All[(i + 1) % All.Count];
    }

    public static string Normalize(string? mode) => All.Contains(mode ?? "") ? mode! : Recent;
}

/// <summary>Favourites, hidden games and sorting of the games widget (pure logic).</summary>
public static class GameOrder
{
    /// <summary>Stable key of a game in the settings ("steam:292030", "riot:valorant.live").</summary>
    public static string Key(InstalledGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.Source + ":" + game.Id;
    }

    /// <summary>
    /// The list to show: hidden games left out (unless <paramref name="showHidden"/>), favourites first, then by the
    /// chosen mode: recently played (Steam's own date or a NotchPC session), name, or total play time recorded.
    /// </summary>
    public static List<InstalledGame> Arrange(
        IEnumerable<InstalledGame> games,
        IReadOnlyCollection<GameSession> history,
        string? mode,
        IEnumerable<string> favourites,
        IEnumerable<string> hidden,
        bool showHidden,
        IReadOnlyList<string>? customOrder = null)
    {
        var fav = new HashSet<string>(favourites, StringComparer.OrdinalIgnoreCase);
        var hide = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        var visible = games.Where(g => showHidden || !hide.Contains(Key(g)));
        var withFavourites = visible.OrderByDescending(g => fav.Contains(Key(g)));

        IOrderedEnumerable<InstalledGame> sorted = GameSortModes.Normalize(mode) switch
        {
            GameSortModes.Name => withFavourites.ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            GameSortModes.Playtime => withFavourites
                .ThenByDescending(g => history.Where(s => Same(s.Game, g.Name)).Sum(s => s.Minutes))
                .ThenByDescending(g => LastPlayed(g, history) ?? DateTimeOffset.MinValue),
            // Own order: as dragged; games not placed yet (new installs) after them, most recent first.
            GameSortModes.Custom => withFavourites
                .ThenBy(g => Position(customOrder, Key(g)))
                .ThenByDescending(g => LastPlayed(g, history) ?? DateTimeOffset.MinValue),
            _ => withFavourites.ThenByDescending(g => LastPlayed(g, history) ?? DateTimeOffset.MinValue),
        };

        return sorted.ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Adds the key when missing, removes it when present (the star / hide buttons).</summary>
    public static List<string> Toggle(IEnumerable<string>? keys, string key)
    {
        var list = (keys ?? []).ToList();
        if (list.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            list.Add(key);
        }

        return list;
    }

    private static DateTimeOffset? LastPlayed(InstalledGame g, IReadOnlyCollection<GameSession> history)
    {
        var mine = GameSessions.LastPlayed(history, g.Name);
        return mine is null ? g.LastPlayed : g.LastPlayed is null ? mine : mine > g.LastPlayed ? mine : g.LastPlayed;
    }

    private static int Position(IReadOnlyList<string>? order, string key)
    {
        if (order is null)
        {
            return int.MaxValue;
        }

        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
