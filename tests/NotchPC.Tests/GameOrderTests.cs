using NotchPC.Core.Games;

namespace NotchPC.Tests;

public sealed class GameOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static InstalledGame Game(string id, string name, int daysAgo) =>
        new(GameSources.Steam, id, name, @"C:\g\" + id, "steam://rungameid/" + id, LastPlayed: Now.AddDays(-daysAgo));

    private static readonly InstalledGame[] Games =
    [
        Game("1", "Witcher", 10),
        Game("2", "Apex", 1),
        Game("3", "Slay the Spire", 5),
    ];

    private static readonly GameSession[] History =
    [
        new("Slay the Spire", Now.AddDays(-20), 600),
        new("Witcher", Now.AddDays(-30), 60),
    ];

    private static List<string> Names(IEnumerable<InstalledGame> games) => games.Select(g => g.Name).ToList();

    [Fact]
    public void Recent_first_by_default()
    {
        Assert.Equal(["Apex", "Slay the Spire", "Witcher"], Names(GameOrder.Arrange(Games, History, null, [], [], false)));
    }

    [Fact]
    public void By_name_and_by_play_time()
    {
        Assert.Equal(["Apex", "Slay the Spire", "Witcher"], Names(GameOrder.Arrange(Games, History, GameSortModes.Name, [], [], false)));
        Assert.Equal(["Slay the Spire", "Witcher", "Apex"], Names(GameOrder.Arrange(Games, History, GameSortModes.Playtime, [], [], false)));
    }

    [Fact]
    public void Favourites_first_hidden_left_out()
    {
        var list = GameOrder.Arrange(Games, History, GameSortModes.Recent, ["steam:1"], ["steam:2"], false);
        Assert.Equal(["Witcher", "Slay the Spire"], Names(list));

        var all = GameOrder.Arrange(Games, History, GameSortModes.Recent, ["steam:1"], ["steam:2"], showHidden: true);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void Own_order_then_new_games_by_recent()
    {
        var list = GameOrder.Arrange(Games, History, GameSortModes.Custom, [], [], false, ["steam:1", "steam:3"]);
        Assert.Equal(["Witcher", "Slay the Spire", "Apex"], Names(list));
    }

    [Fact]
    public void Toggle_adds_and_removes()
    {
        var once = GameOrder.Toggle(null, "steam:1");
        Assert.Equal(["steam:1"], once);
        Assert.Empty(GameOrder.Toggle(once, "STEAM:1"));
    }

    [Fact]
    public void Sort_modes_cycle()
    {
        Assert.Equal(GameSortModes.Name, GameSortModes.Next(GameSortModes.Recent));
        Assert.Equal(GameSortModes.Custom, GameSortModes.Next(GameSortModes.Playtime));
        Assert.Equal(GameSortModes.Recent, GameSortModes.Next(GameSortModes.Custom));
        Assert.Equal(GameSortModes.Name, GameSortModes.Next("bogus"));
    }
}
