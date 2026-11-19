using System.Globalization;
using NotchPC.Core.Games;

namespace NotchPC.Tests;

public sealed class GameLibraryTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Moje\\Steam"
        		"apps"
        		{
        			"228980"		"445118522"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        	}
        }
        """;

    private static string Manifest(string id, string name, string flags = "4", string played = "1766966959") => $$"""
        "AppState"
        {
        	"appid"		"{{id}}"
        	"name"		"{{name}}"
        	"StateFlags"		"{{flags}}"
        	"installdir"		"{{name}}"
        	"LastPlayed"		"{{played}}"
        }
        """;

    [Fact]
    public void Vdf_reads_nested_blocks_and_escapes()
    {
        var root = Vdf.Parse(LibraryFolders);
        Assert.Equal(@"C:\Moje\Steam", root.Child("libraryfolders")!.Child("0")!["path"]);
        Assert.Equal("445118522", root.Child("libraryfolders")!.Child("0")!.Child("apps")!["228980"]);
    }

    [Fact]
    public void Steam_libraries_include_the_steam_folder_once()
    {
        var libs = GameLibrary.SteamLibraries(LibraryFolders, "c:/moje/steam");
        Assert.Equal(2, libs.Count);
        Assert.Equal(@"c:\moje\steam", libs[0]);
        Assert.Equal(@"D:\SteamLibrary", libs[1]);
    }

    [Fact]
    public void Steam_manifest_gives_a_game_with_its_folder_and_launch_uri()
    {
        var g = GameLibrary.SteamGame(Manifest("1794680", "Vampire Survivors"), @"C:\Moje\Steam")!;
        Assert.Equal("Vampire Survivors", g.Name);
        Assert.Equal(@"C:\Moje\Steam\steamapps\common\Vampire Survivors", g.InstallDir);
        Assert.Equal("steam://rungameid/1794680", g.Launch);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1766966959), g.LastPlayed);
    }

    [Theory]
    [InlineData("228980", "Steamworks Common Redistributables", "4")]
    [InlineData("1493710", "Proton Experimental", "4")]
    [InlineData("12345", "Some Game", "1026")] // downloading, not installed yet (bit 4 missing)
    public void Steam_tools_and_unfinished_installs_are_skipped(string id, string name, string flags) =>
        Assert.Null(GameLibrary.SteamGame(Manifest(id, name, flags), @"C:\Steam"));

    [Fact]
    public void Epic_game_manifest_gives_a_launcher_uri()
    {
        const string json = """
            {"DisplayName":"Sol Cesto","AppName":"e1b0","CatalogNamespace":"ce5d","CatalogItemId":"b85e",
             "InstallLocation":"C:\\Epic\\SolCesto","LaunchExecutable":"SolCesto.exe","bIsApplication":true,"AppCategories":["games","applications"]}
            """;
        var g = GameLibrary.EpicGame(json)!;
        Assert.Equal("Sol Cesto", g.Name);
        Assert.Equal("com.epicgames.launcher://apps/ce5d%3Ab85e%3Ae1b0?action=launch&silent=true", g.Launch);
    }

    [Fact]
    public void Epic_engine_and_plugins_are_skipped()
    {
        Assert.Null(GameLibrary.EpicGame("""{"DisplayName":"Unreal Engine","AppName":"UE_5.5","CatalogNamespace":"ue","InstallLocation":"C:\\UE","LaunchExecutable":"x.exe","bIsApplication":false,"AppCategories":["engines"]}"""));
        Assert.Null(GameLibrary.EpicGame("not json"));
    }

    [Fact]
    public void Exe_is_matched_to_the_game_folder()
    {
        InstalledGame[] games =
        [
            new(GameSources.Steam, "1", "Outer", @"C:\Games", "x"),
            new(GameSources.Steam, "2", "Inner", @"C:\Games\Inner", "y"),
        ];
        Assert.Equal("Inner", GameLibrary.ForExe(games, @"C:\Games\Inner\bin\game.exe")!.Name);
        Assert.Null(GameLibrary.ForExe(games, @"C:\GamesX\game.exe"));
    }

    [Fact]
    public void Gog_entry_needs_a_name_and_an_exe() =>
        Assert.Null(GameLibrary.GogGame("1", "Game", @"C:\GOG\Game", null, null));
}

public sealed class GameSessionTests
{
    [Fact]
    public void Fps_samples_skip_zeros_and_give_low_percentile()
    {
        var s = new FpsSamples();
        s.Add(0);
        for (var i = 1; i <= 100; i++)
        {
            s.Add(i);
        }

        Assert.Equal(100, s.Count);
        Assert.Equal(50.5, s.Average!.Value, 6);
        Assert.Equal(5, s.Low);
        Assert.Equal(100, s.Max);
        Assert.Null(new FpsSamples().Average);
    }

    [Fact]
    public void Week_starts_on_monday()
    {
        var thursday = new DateTimeOffset(2026, 11, 19, 21, 30, 0, TimeSpan.FromHours(1));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.FromHours(1)), GameSessions.WeekStart(thursday));
        var sunday = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal(16, GameSessions.WeekStart(sunday).Day);
    }

    [Fact]
    public void Minutes_and_last_played_per_game()
    {
        var monday = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        GameSession[] h =
        [
            new("A", monday.AddDays(-1), 60),
            new("A", monday.AddHours(20), 30),
            new("b", monday.AddHours(21), 45),
        ];
        Assert.Equal(75, GameSessions.MinutesSince(h, monday));
        Assert.Equal(45, GameSessions.MinutesSince(h, monday, "B"));
        Assert.Equal(monday.AddHours(20).AddMinutes(30), GameSessions.LastPlayed(h, "a"));
        Assert.Null(GameSessions.LastPlayed(h, "c"));
    }

    [Fact]
    public void History_keeps_the_newest()
    {
        var list = new List<GameSession>();
        for (var i = 0; i < GameSessions.Keep + 5; i++)
        {
            list = GameSessions.Add(list, new GameSession("G", DateTimeOffset.UnixEpoch.AddHours(i), 10));
        }

        Assert.Equal(GameSessions.Keep, list.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(5), list[0].Start);
    }

    [Theory]
    [InlineData(134, "2 h 14 min")]
    [InlineData(45, "45 min")]
    [InlineData(120, "2 h")]
    public void Durations(double minutes, string text) => Assert.Equal(text, GameSessions.Duration(minutes));

    [Fact]
    public void Fps_text() => Assert.Equal("143", GameSessions.Fps(142.6, CultureInfo.InvariantCulture));
}
