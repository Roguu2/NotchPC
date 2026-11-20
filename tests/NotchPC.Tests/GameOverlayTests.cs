using NotchPC.Core.Games;
using NotchPC.Core.Layout;
using NotchPC.Core.Settings;

namespace NotchPC.Tests;

public sealed class GameOverlayTests
{
    [Fact]
    public void Fullscreen_app_is_a_game_unless_ignored()
    {
        Assert.True(GameDetection.IsGame("Overwatch", coversMonitor: true, autoFullscreen: true, [], GameDetection.DefaultIgnored));
        Assert.False(GameDetection.IsGame("chrome", coversMonitor: true, autoFullscreen: true, [], GameDetection.DefaultIgnored));
        Assert.False(GameDetection.IsGame("Overwatch", coversMonitor: false, autoFullscreen: true, [], []));
        Assert.False(GameDetection.IsGame("Overwatch", coversMonitor: true, autoFullscreen: false, [], []));
    }

    [Fact]
    public void Lock_screen_and_chat_apps_are_not_games()
    {
        Assert.False(GameDetection.IsGame("LockApp", coversMonitor: true, autoFullscreen: true, [], []));
        Assert.False(GameDetection.IsGame("LockApp", coversMonitor: true, autoFullscreen: true, ["LockApp"], []));
        Assert.False(GameDetection.IsGame("Discord", coversMonitor: true, autoFullscreen: true, [], []));
        Assert.True(GameDetection.IsGame("Discord", coversMonitor: false, autoFullscreen: true, ["discord"], []));
    }

    [Fact]
    public void Listed_game_shows_also_windowed_and_ignored_wins()
    {
        Assert.True(GameDetection.IsGame("Minecraft.exe", coversMonitor: false, autoFullscreen: false, ["minecraft"], []));
        Assert.False(GameDetection.IsGame("Minecraft", coversMonitor: true, autoFullscreen: true, ["Minecraft"], ["MINECRAFT.exe"]));
        Assert.False(GameDetection.IsGame("", coversMonitor: true, autoFullscreen: true, [], []));
    }

    [Fact]
    public void Shortcut_moves_the_app_between_the_lists()
    {
        var (games, ignored) = GameDetection.Toggle("Overwatch.exe", shownNow: true, ["Overwatch"], ["vlc"]);
        Assert.Empty(games);
        Assert.Equal(["vlc", "Overwatch"], ignored);

        (games, ignored) = GameDetection.Toggle("overwatch", shownNow: false, games, ignored);
        Assert.Equal(["overwatch"], games);
        Assert.Equal(["vlc"], ignored);
    }

    [Fact]
    public void Lists_are_cleaned()
    {
        Assert.Equal(["Game", "other"], GameDetection.Normalize([" Game.exe ", "game", "", "  ", "other"]));
    }

    [Theory]
    [InlineData(null, "—", FpsLevel.None)]
    [InlineData(0.2, "—", FpsLevel.None)]
    [InlineData(24.4, "24", FpsLevel.Low)]
    [InlineData(45.0, "45", FpsLevel.Fair)]
    [InlineData(59.6, "60", FpsLevel.Good)]
    [InlineData(143.7, "144", FpsLevel.Good)]
    public void Fps_text_and_colour(double? fps, string text, FpsLevel level)
    {
        Assert.Equal(text, OverlayFormat.Fps(fps));
        Assert.Equal(level, OverlayFormat.Level(fps));
    }

    [Fact]
    public void Load_and_temperature_text()
    {
        Assert.Equal("23% · 54°", OverlayFormat.LoadAndTemp(23.4, 54.4));
        Assert.Equal("23%", OverlayFormat.LoadAndTemp(23.4, null));
        Assert.Equal("—", OverlayFormat.LoadAndTemp(null, null));
    }

    [Theory]
    [InlineData("ctrl+alt+f", "Ctrl+Alt+F", 3u, 0x46u)]
    [InlineData("ctrl+shift+f12", "Ctrl+Shift+F12", 6u, 0x7Bu)]
    [InlineData("alt+f12", "Alt+F12", 1u, 0x7Bu)]
    [InlineData("bogus", "Ctrl+Alt+F", 3u, 0x46u)]
    public void Shortcuts(string id, string display, uint modifiers, uint key)
    {
        Assert.Equal(display, OverlayHotkeys.Display(id));
        Assert.Equal((modifiers, key), OverlayHotkeys.Keys(id));
    }

    [Fact]
    public void No_shortcut()
    {
        Assert.Null(OverlayHotkeys.Keys(OverlayHotkeys.None));
        Assert.Equal("", OverlayHotkeys.Display(OverlayHotkeys.None));
    }

    [Theory]
    [InlineData("top-left", 1934, 14)]
    [InlineData("top-right", 4446, 14)]
    [InlineData("bottom-left", 1934, 1386)]
    [InlineData("bottom-right", 4446, 1386)]
    [InlineData("nonsense", 1934, 14)]
    public void Corner_placement(string position, int x, int y)
    {
        var monitor = new ScreenRect(1920, 0, 4480, 1440);
        Assert.Equal((x, y), OverlayPositions.Place(position, monitor, 20, 40, 14));
    }

    [Fact]
    public void Settings_store_cleans_overlay_values()
    {
        var s = new AppSettings();
        s.GameOverlay.Position = "middle";
        s.GameOverlay.Hotkey = "f13";
        s.GameOverlay.Games = ["a.exe", "A", ""];

        SettingsStore.Normalize(s);

        Assert.Equal("top-left", s.GameOverlay.Position);
        Assert.Equal("ctrl+alt+f", s.GameOverlay.Hotkey);
        Assert.Equal(["a"], s.GameOverlay.Games);
        Assert.Contains("chrome", s.GameOverlay.Ignored);
    }
}
