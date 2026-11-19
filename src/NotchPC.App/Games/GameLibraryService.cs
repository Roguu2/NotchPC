using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Games;
using NotchPC.Core.Settings;

namespace NotchPC.App.Games;

/// <summary>
/// Installed games from Steam, the Epic Games launcher and GOG, read from their own files and registry entries
/// (a few small files, on a background thread). Cached for a couple of minutes.
/// </summary>
internal sealed class GameLibraryService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private IReadOnlyList<InstalledGame> _games = [];
    private DateTime _loaded;
    private Task<IReadOnlyList<InstalledGame>>? _loading;

    public IReadOnlyList<InstalledGame> Cached => _games;

    public async Task<IReadOnlyList<InstalledGame>> GetAsync(bool fresh = false)
    {
        if (!fresh && _loaded != default && DateTime.UtcNow - _loaded < CacheFor)
        {
            return _games;
        }

        _loading ??= Task.Run(Scan);
        try
        {
            _games = await _loading.ConfigureAwait(true);
            _loaded = DateTime.UtcNow;
        }
        finally
        {
            _loading = null;
        }

        return _games;
    }

    /// <summary>The installed game a running exe belongs to (by its folder).</summary>
    public InstalledGame? ForExe(string? exe) => GameLibrary.ForExe(_games, exe);

    private static IReadOnlyList<InstalledGame> Scan()
    {
        var list = new List<InstalledGame>();
        var installed = new List<UninstallEntry>();
        Try("installed apps", () => installed.AddRange(UninstallEntries()));
        Try("Steam", () => Steam(list, installed));
        Try("Epic", () => Epic(list));
        Try("GOG", () => Gog(list, installed));
        Try("other launchers", () =>
        {
            var battleNet = GameLibrary.BattleNetExe(installed);
            foreach (var e in installed)
            {
                if (GameLibrary.FromUninstall(e, p => File.Exists(p) || Directory.Exists(p), battleNet) is { } game)
                {
                    list.Add(game);
                }
            }
        });
        AppLog.Info($"Game library: {list.Count} games ({string.Join(", ", list.GroupBy(g => g.Source).Select(g => $"{g.Key} {g.Count()}"))})");
        return GameLibrary.Merge(list);
    }

    /// <summary>The Windows installed-apps list (machine and user, 64- and 32-bit views).</summary>
    private static List<UninstallEntry> UninstallEntries()
    {
        var result = new List<UninstallEntry>();
        (RegistryKey Root, string Path)[] places =
        [
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        ];
        foreach (var (root, path) in places)
        {
            using var list = root.OpenSubKey(path);
            if (list is null)
            {
                continue;
            }

            foreach (var name in list.GetSubKeyNames())
            {
                using var k = list.OpenSubKey(name);
                if (k is null)
                {
                    continue;
                }

                result.Add(new UninstallEntry(
                    name,
                    k.GetValue("DisplayName") as string,
                    k.GetValue("Publisher") as string,
                    k.GetValue("InstallLocation") as string,
                    k.GetValue("DisplayIcon") as string,
                    k.GetValue("UninstallString") as string));
            }
        }

        return result;
    }

    private static string? IconOf(List<UninstallEntry> installed, string key) =>
        installed.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) is { } e
        && GameLibrary.IconPath(e.DisplayIcon) is { } icon && File.Exists(icon)
            ? icon
            : null;

    private static void Try(string source, Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            AppLog.Error($"Reading {source} games failed", ex);
        }
    }

    private static void Steam(List<InstalledGame> list, List<UninstallEntry> installed)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string { Length: > 0 } raw)
        {
            return;
        }

        var steam = Path.GetFullPath(raw);
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libraries = File.Exists(vdf) ? GameLibrary.SteamLibraries(File.ReadAllText(vdf), steam) : [steam];
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps))
            {
                continue;
            }

            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                if (GameLibrary.SteamGame(ReadOrEmpty(acf), library) is { } game)
                {
                    // Cover (also the localised copies in sub-folders), else Steam's .ico of the game, else its tiny .jpg.
                    var image = GameLibrary.SteamImageCandidates(steam, game.Id).FirstOrDefault(File.Exists)
                                ?? SteamCover(steam, game.Id)
                                ?? IconOf(installed, "Steam App " + game.Id)
                                ?? SteamIcon(steam, game.Id);
                    list.Add(game with { Image = image });
                }
            }
        }
    }

    private static void Epic(List<InstalledGame> list)
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
        {
            return;
        }

        foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
        {
            if (GameLibrary.EpicGame(ReadOrEmpty(item)) is { } game && Directory.Exists(game.InstallDir))
            {
                list.Add(game);
            }
        }
    }

    private static void Gog(List<InstalledGame> list, List<UninstallEntry> installed)
    {
        foreach (var path in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            using var games = Registry.LocalMachine.OpenSubKey(path);
            if (games is null)
            {
                continue;
            }

            foreach (var id in games.GetSubKeyNames())
            {
                using var g = games.OpenSubKey(id);
                if (g is null)
                {
                    continue;
                }

                var game = GameLibrary.GogGame(id, g.GetValue("gameName") as string, g.GetValue("path") as string, g.GetValue("exe") as string, g.GetValue("workingDir") as string);
                if (game is not null && File.Exists(game.Launch))
                {
                    list.Add(game with { Image = IconOf(installed, id + "_is1") ?? game.Image });
                }
            }
        }
    }

    /// <summary>Newer Steam keeps covers in hashed, per-language sub-folders of the app's cache folder.</summary>
    private static string? SteamCover(string steam, string appId)
    {
        var dir = Path.Combine(steam, "appcache", "librarycache", appId);
        return Directory.Exists(dir) ? GameLibrary.PickSteamImage(Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)) : null;
    }

    /// <summary>Without a cover Steam's cache still has the game's small icon (a .jpg with a hashed name).</summary>
    private static string? SteamIcon(string steam, string appId)
    {
        var dir = Path.Combine(steam, "appcache", "librarycache", appId);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.jpg").FirstOrDefault() : null;
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}

/// <summary>Finished play sessions, kept in %AppData%\NotchPC\game-sessions.json (names, times, FPS; nothing else).</summary>
internal sealed class GameHistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(SettingsStore.DefaultDirectory, "game-sessions.json");

    public GameHistoryStore()
    {
        try
        {
            if (File.Exists(_path))
            {
                Sessions = JsonSerializer.Deserialize<List<GameSession>>(File.ReadAllText(_path), Json) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Error("Reading the game sessions failed", ex);
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<GameSession> Sessions { get; private set; } = [];

    public void Add(GameSession session)
    {
        Sessions = GameSessions.Add(Sessions, session);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Sessions, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Saving the game sessions failed", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
