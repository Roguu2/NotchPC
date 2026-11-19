using NotchPC.Core.Settings;

namespace NotchPC.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NotchPC.Tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults_with_all_tabs_enabled()
    {
        var s = new SettingsStore(FilePath).Load();

        Assert.Equal("", s.Language);
        Assert.Equal("", s.Notch.Monitor);
        Assert.True(s.Notch.HideOnFullscreen);
        Assert.Equal(TabIds.DefaultOrder, s.Tabs.Select(t => t.Id));
        Assert.All(s.Tabs, t => Assert.True(t.Enabled));
    }

    [Fact]
    public void Save_then_load_round_trips_values()
    {
        var store = new SettingsStore(FilePath);
        var s = store.Load();
        s.Language = "pl";
        s.Notch.Monitor = @"\\.\DISPLAY2";
        s.Notch.HoverDelayMs = 250;
        s.Tabs[0] = s.Tabs[0] with { Enabled = false };

        store.Save(s);
        var loaded = store.Load();

        Assert.Equal("pl", loaded.Language);
        Assert.Equal(@"\\.\DISPLAY2", loaded.Notch.Monitor);
        Assert.Equal(250, loaded.Notch.HoverDelayMs);
        Assert.False(loaded.Tabs[0].Enabled);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_returned()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not json");

        var s = new SettingsStore(FilePath).Load();

        Assert.Equal(TabIds.DefaultOrder.Count, s.Tabs.Count);
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "notch": { "hoverDelayMs": -5, "collapseDelayMs": 99999, "topOffset": 500 } }""");

        var s = new SettingsStore(FilePath).Load();

        Assert.Equal(0, s.Notch.HoverDelayMs);
        Assert.Equal(5000, s.Notch.CollapseDelayMs);
        Assert.Equal(64, s.Notch.TopOffset);
    }

    [Fact]
    public void Tabs_are_normalized_unknown_dropped_duplicates_removed_missing_appended()
    {
        var input = new[]
        {
            new TabSetting(TabIds.Phone, true),
            new TabSetting("removed-in-old-version", true),
            new TabSetting(TabIds.Music, false),
            new TabSetting(TabIds.Phone, false),
        };

        var result = SettingsStore.NormalizeTabs(input);

        Assert.Equal(TabIds.Phone, result[0].Id);
        Assert.True(result[0].Enabled);
        Assert.Equal(TabIds.Music, result[1].Id);
        Assert.False(result[1].Enabled);
        Assert.Equal(TabIds.DefaultOrder.Count, result.Count);
        Assert.Equal(TabIds.DefaultOrder.Order(), result.Select(t => t.Id).Order());
    }

    [Fact]
    public void Retired_apps_and_games_tabs_turn_on_their_widgets()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"tabs":[{"id":"apps","enabled":true},{"id":"games","enabled":false},{"id":"notes","enabled":true}]}""");

        var s = new SettingsStore(FilePath).Load();

        Assert.True(s.Widgets.AppsEnabled);
        Assert.False(s.Widgets.GamesEnabled);
        Assert.DoesNotContain(s.Tabs, t => t.Id is TabIds.RetiredApps or TabIds.RetiredGames or TabIds.RetiredNotes);
    }

    [Fact]
    public void File_from_stage_1_without_music_section_gets_music_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "schemaVersion": 1, "language": "pl", "notch": { "monitor": "" } }""");

        var s = new SettingsStore(FilePath).Load();

        Assert.True(s.Music.ShowMiniPlayer);
        Assert.True(s.Music.AnimateMiniPlayer);
        Assert.False(s.Music.LyricsOpen);
    }

    [Fact]
    public void Widget_settings_are_normalized()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
            { "widgets": { "focusMinutes": 1, "breakMinutes": 999, "weatherLatitude": 50.0,
                           "positions": { "timer": { "monitor": "", "x": 1, "y": 2 }, "stats": { "monitor": "\\\\.\\DISPLAY1", "x": 10, "y": 20 } } } }
            """);

        var w = new SettingsStore(FilePath).Load().Widgets;

        Assert.Equal(5, w.FocusMinutes);
        Assert.Equal(120, w.BreakMinutes);
        Assert.Null(w.WeatherLatitude); // half a coordinate is no place
        Assert.Equal(["stats"], w.Positions.Keys);
        Assert.True(w.TimerEnabled && w.WeatherEnabled && w.CalendarEnabled && w.StatsEnabled);
    }

    [Fact]
    public void Settings_tab_cannot_be_disabled()
    {
        var result = SettingsStore.NormalizeTabs([new TabSetting(TabIds.Settings, false)]);

        Assert.True(result.Single(t => t.Id == TabIds.Settings).Enabled);
    }
}
