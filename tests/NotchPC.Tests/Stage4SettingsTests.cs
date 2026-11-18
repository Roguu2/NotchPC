using NotchPC.Core.Settings;

namespace NotchPC.Tests;

/// <summary>Settings of stage 4 (clipboard, shelf, apps) coming from older or hand-edited files.</summary>
public sealed class Stage4SettingsTests : IDisposable
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
    public void File_from_stage_3_gets_stage_4_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "language": "pl", "widgets": { "timerEnabled": false } }""");

        var s = new SettingsStore(FilePath).Load();

        Assert.True(s.Clipboard.Enabled);
        Assert.Equal(100, s.Clipboard.Limit);
        Assert.Empty(s.Shelf.Files);
        Assert.Equal("", s.Apps.ScreenshotFolder);
        Assert.Null(s.Apps.Shortcuts); // null = the app fills in default shortcuts once
    }

    [Fact]
    public void Broken_values_are_fixed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
            {
              "clipboard": { "limit": 100000 },
              "shelf": { "files": [ "C:\\a.txt", "c:\\A.TXT", "", "C:\\b.txt" ] },
              "apps": { "screenshotFolder": null, "shortcuts": [ { "name": "x", "target": "" }, null, { "name": "Paint", "target": "mspaint.exe" } ] }
            }
            """);

        var s = new SettingsStore(FilePath).Load();

        Assert.Equal(500, s.Clipboard.Limit);
        Assert.Equal(["C:\\a.txt", "C:\\b.txt"], s.Shelf.Files);
        Assert.Equal("", s.Apps.ScreenshotFolder);
        var shortcut = Assert.Single(s.Apps.Shortcuts!);
        Assert.Equal("mspaint.exe", shortcut.Target);
    }

    [Fact]
    public void Shelf_and_shortcuts_round_trip()
    {
        var store = new SettingsStore(FilePath);
        var s = store.Load();
        s.Shelf.Files = ["D:\\film.mp4"];
        s.Apps.Shortcuts = [new AppShortcut { Name = "Notatnik", Target = "notepad.exe" }];
        s.Clipboard.Enabled = false;
        store.Save(s);

        var loaded = store.Load();

        Assert.Equal(["D:\\film.mp4"], loaded.Shelf.Files);
        Assert.Equal("Notatnik", Assert.Single(loaded.Apps.Shortcuts!).Name);
        Assert.False(loaded.Clipboard.Enabled);
    }
}
