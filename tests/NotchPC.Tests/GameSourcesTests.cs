using NotchPC.Core.Games;

namespace NotchPC.Tests;

public sealed class GameSourcesTests
{
    private static bool Yes(string _) => true;

    [Fact]
    public void Riot_games_start_through_the_riot_client()
    {
        var g = GameLibrary.FromUninstall(new UninstallEntry(
            "Riot Game valorant.live", "VALORANT", "Riot Games, Inc", "C:/Riot Games/VALORANT/live",
            "C:/ProgramData/Riot Games/Metadata/valorant.live/valorant.live.ico",
            @"""C:\Riot Games\Riot Client\RiotClientServices.exe"" --uninstall-product=valorant --uninstall-patchline=live"), Yes)!;

        Assert.Equal(GameSources.Riot, g.Source);
        Assert.Equal(@"C:\Riot Games\Riot Client\RiotClientServices.exe", g.Launch);
        Assert.Equal("--launch-product=valorant --launch-patchline=live", g.LaunchArgs);
        Assert.Equal(@"C:\ProgramData\Riot Games\Metadata\valorant.live\valorant.live.ico", g.Image);
        Assert.Equal(@"C:\Riot Games\VALORANT\live", g.InstallDir);
    }

    [Fact]
    public void Riot_client_and_launchers_are_not_games()
    {
        Assert.Null(GameLibrary.FromUninstall(new UninstallEntry("Riot Game Riot_Client.", "Riot Client", "Riot Games, Inc", "C:/Riot Games/Riot Client/", null, @"""C:\x.exe"""), Yes));
        Assert.Null(GameLibrary.FromUninstall(new UninstallEntry("Battle.net", "Battle.net", "Blizzard Entertainment", @"C:\BNet", @"C:\BNet\Battle.net.exe", null), Yes));
        Assert.Null(GameLibrary.FromUninstall(new UninstallEntry("{guid}", "EA app", "Electronic Arts", @"C:\EA", @"C:\EA\EADesktop.exe", null), Yes));
    }

    [Fact]
    public void Blizzard_games_use_their_battlenet_code()
    {
        var g = GameLibrary.FromUninstall(new UninstallEntry(
            "Overwatch", "Overwatch", "Blizzard Entertainment", @"D:\wow\Overwatch", @"D:\wow\Overwatch\_retail_\Overwatch.exe",
            @"""C:\ProgramData\Battle.net\Agent\Blizzard Uninstaller.exe"" --lang=plPL --uid=prometheus --displayname=""Overwatch"""), Yes)!;
        Assert.Equal("battlenet://Pro", g.Launch);
        Assert.Equal(GameSources.Blizzard, g.Source);

        var withLauncher = GameLibrary.FromUninstall(new UninstallEntry(
            "Overwatch", "Overwatch", "Blizzard Entertainment", @"D:\wow\Overwatch", null, "x --uid=prometheus"), Yes, @"C:\BNet\Battle.net.exe")!;
        Assert.Equal(@"C:\BNet\Battle.net.exe", withLauncher.Launch);
        Assert.Equal("--exec=\"launch Pro\"", withLauncher.LaunchArgs);
    }

    [Fact]
    public void Battle_net_path_comes_from_its_own_entry()
    {
        UninstallEntry[] entries =
        [
            new("Overwatch", "Overwatch", "Blizzard Entertainment", @"D:\wow\Overwatch", null, null),
            new("Battle.net", "Battle.net", "Blizzard Entertainment", @"C:\Program Files (x86)\Battle.net", @"C:\Program Files (x86)\Battle.net\Battle.net.exe", null),
        ];
        Assert.Equal(@"C:\Program Files (x86)\Battle.net\Battle.net.exe", GameLibrary.BattleNetExe(entries));
    }

    [Fact]
    public void Ubisoft_and_minecraft()
    {
        var u = GameLibrary.FromUninstall(new UninstallEntry("Uplay Install 1803", "Far Cry 5", "Ubisoft", @"C:\Games\FC5", null, null), Yes)!;
        Assert.Equal("uplay://launch/1803/0", u.Launch);

        var m = GameLibrary.FromUninstall(new UninstallEntry("{guid}", "Minecraft Launcher", "Mojang", @"C:\Program Files (x86)\Minecraft Launcher\", null, null), Yes)!;
        Assert.Equal("Minecraft", m.Name);
        Assert.Equal(@"C:\Program Files (x86)\Minecraft Launcher\MinecraftLauncher.exe", m.Launch);
    }

    [Fact]
    public void Deleted_games_are_skipped() =>
        Assert.Null(GameLibrary.FromUninstall(new UninstallEntry("Uplay Install 1803", "Far Cry 5", "Ubisoft", @"C:\Gone", null, null), _ => false));

    [Theory]
    [InlineData(@"C:\x\game.ico,0", @"C:\x\game.ico")]
    [InlineData(@"""C:\a b\game.exe""", @"C:\a b\game.exe")]
    [InlineData("C:/x/y.ico", @"C:\x\y.ico")]
    [InlineData("", null)]
    public void Icon_paths(string raw, string? path) => Assert.Equal(path, GameLibrary.IconPath(raw));

    [Fact]
    public void Steam_cover_prefers_the_header_also_in_language_folders()
    {
        Assert.Equal(@"abc\library_header_polish.jpg", GameLibrary.PickSteamImage(
            [@"abc\library_hero_blur_polish.jpg", @"abc\library_header_polish.jpg", @"def\logo_polish.png", "x.jpg"]));
        Assert.Equal("header.jpg", GameLibrary.PickSteamImage(["library_hero.jpg", "header.jpg"]));
        Assert.Null(GameLibrary.PickSteamImage(["7b52d11e.jpg", "logo.png"]));
    }
}
