using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NotchPC.Core.Games;

/// <summary>Where a game comes from.</summary>
public static class GameSources
{
    public const string Steam = "steam";
    public const string Epic = "epic";
    public const string Gog = "gog";
    public const string Riot = "riot";
    public const string Blizzard = "battlenet";
    public const string Ubisoft = "ubisoft";
    public const string Ea = "ea";
    public const string Rockstar = "rockstar";
    public const string Minecraft = "minecraft";

    /// <summary>"Steam", "Battle.net" … for the tile subtitle.</summary>
    public static string DisplayName(string source) => source switch
    {
        Steam => "Steam",
        Epic => "Epic Games",
        Gog => "GOG",
        Riot => "Riot Games",
        Blizzard => "Battle.net",
        Ubisoft => "Ubisoft Connect",
        Ea => "EA app",
        Rockstar => "Rockstar Games",
        Minecraft => "Minecraft",
        _ => source,
    };
}

/// <summary>
/// An installed game: how to start it (a launcher URI or the game's exe), where it lives (to recognise its
/// process later) and an optional picture (a cover .jpg, or an .exe whose icon is shown).
/// </summary>
public sealed record InstalledGame(
    string Source,
    string Id,
    string Name,
    string InstallDir,
    string Launch,
    string? WorkingDir = null,
    string? Image = null,
    DateTimeOffset? LastPlayed = null,
    string? LaunchArgs = null);

/// <summary>One entry of the Windows "installed apps" list (the Uninstall registry keys).</summary>
public sealed record UninstallEntry(
    string Key,
    string? Name,
    string? Publisher,
    string? InstallLocation,
    string? DisplayIcon,
    string? UninstallString);

/// <summary>Valve's KeyValues text format (libraryfolders.vdf, appmanifest_*.acf): quoted keys, values and { } blocks.</summary>
public sealed class Vdf
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Vdf> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key] => Values.TryGetValue(key, out var v) ? v : null;

    public Vdf? Child(string key) => Children.TryGetValue(key, out var c) ? c : null;

    /// <summary>Parses the text; broken input gives whatever was read up to that point.</summary>
    public static Vdf Parse(string text)
    {
        var tokens = Tokenize(text ?? "");
        var i = 0;
        return ParseBlock(tokens, ref i);
    }

    private static Vdf ParseBlock(List<Token> tokens, ref int i)
    {
        var node = new Vdf();
        while (i < tokens.Count)
        {
            var key = tokens[i++];
            if (key.Kind != TokenKind.Text)
            {
                if (key.Kind == TokenKind.Close)
                {
                    return node;
                }

                continue; // stray "{"
            }

            if (i >= tokens.Count)
            {
                break;
            }

            var next = tokens[i++];
            switch (next.Kind)
            {
                case TokenKind.Open:
                    node.Children[key.Text] = ParseBlock(tokens, ref i);
                    break;
                case TokenKind.Text:
                    node.Values[key.Text] = next.Text;
                    break;
                default:
                    return node;
            }
        }

        return node;
    }

    /// <summary>Quoted or bare strings, "{" and "}". Comments (//) are skipped.</summary>
    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '{')
            {
                tokens.Add(new Token(TokenKind.Open, ""));
                i++;
            }
            else if (c == '}')
            {
                tokens.Add(new Token(TokenKind.Close, ""));
                i++;
            }
            else if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                        sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                    }
                    else
                    {
                        sb.Append(s[i]);
                    }

                    i++;
                }

                i++;
                tokens.Add(new Token(TokenKind.Text, sb.ToString()));
            }
            else
            {
                var start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Text, s[start..i]));
            }
        }

        return tokens;
    }

    private enum TokenKind
    {
        Text,
        Open,
        Close,
    }

    private readonly record struct Token(TokenKind Kind, string Text);
}

/// <summary>Reads what Steam, Epic and GOG write about installed games (pure parsing; the app reads the files).</summary>
public static class GameLibrary
{
    // Steam "apps" that are not games.
    private static readonly HashSet<string> SteamTools = new(StringComparer.Ordinal)
    {
        "228980", "1070560", "1391110", "1628350", "1493710", "1826330", "2180100", "250820",
    };

    private static readonly string[] ToolWords =
    [
        "Redistributable", "Steam Linux Runtime", "Proton", "SteamVR", "Dedicated Server", " SDK", "Steamworks",
    ];

    /// <summary>Library folders from libraryfolders.vdf (the Steam folder itself is always one of them).</summary>
    public static List<string> SteamLibraries(string libraryFoldersVdf, string steamPath)
    {
        var list = new List<string> { steamPath };
        var root = Vdf.Parse(libraryFoldersVdf);
        var folders = root.Child("libraryfolders") ?? root;
        foreach (var child in folders.Children.Values)
        {
            if (child["path"] is { Length: > 0 } p)
            {
                list.Add(p);
            }
        }

        return list
            .Select(p => p.Replace('/', '\\').TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>One appmanifest_*.acf → a game (null for tools, redistributables and unfinished installs).</summary>
    public static InstalledGame? SteamGame(string acf, string libraryPath)
    {
        var state = Vdf.Parse(acf).Child("AppState");
        if (state?["appid"] is not { Length: > 0 } id || state["name"] is not { Length: > 0 } name || state["installdir"] is not { Length: > 0 } dir)
        {
            return null;
        }

        // StateFlags 4 = fully installed (more bits while updating, still playable).
        if (!int.TryParse(state["StateFlags"], CultureInfo.InvariantCulture, out var flags) || (flags & 4) == 0)
        {
            return null;
        }

        if (SteamTools.Contains(id) || ToolWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        DateTimeOffset? played = long.TryParse(state["LastPlayed"], CultureInfo.InvariantCulture, out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
        return new InstalledGame(
            GameSources.Steam,
            id,
            name,
            Path.Combine(libraryPath.TrimEnd('\\', '/'), "steamapps", "common", dir),
            "steam://rungameid/" + id,
            LastPlayed: played);
    }

    /// <summary>Battle.net product codes (uid in the uninstaller → battlenet:// launch code).</summary>
    private static readonly Dictionary<string, string> BlizzardCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prometheus"] = "Pro", ["wow"] = "WoW", ["wow_classic"] = "WoWC", ["wow_classic_era"] = "WoWC", ["fenris"] = "Fen",
        ["hs_beta"] = "WTCG", ["diablo3"] = "D3", ["s2"] = "S2", ["s1"] = "S1", ["heroes"] = "Hero", ["w3"] = "W3",
        ["osi"] = "OSI", ["anbs"] = "ANBS", ["odin"] = "ODIN", ["viper"] = "VIPR", ["lazarus"] = "LAZR", ["fore"] = "FORE",
        ["aqua"] = "AQUA", ["auks"] = "AUKS", ["zeus"] = "ZEUS", ["rtro"] = "RTRO", ["wlby"] = "WLBY",
    };

    private static readonly HashSet<string> LauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Battle.net", "EA app", "Origin", "Rockstar Games Launcher", "Rockstar Games SDK", "Rockstar Games Social Club",
        "Ubisoft Connect", "Uplay", "Riot Client", "Riot Vanguard",
    };

    /// <summary>"C:\x\game.ico,0" or "\"C:\x\game.exe\"" → the file path.</summary>
    public static string? IconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var s = displayIcon.Trim();
        var comma = s.LastIndexOf(',');
        if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            s = s[..comma];
        }

        s = s.Trim().Trim('"').Replace('/', '\\');
        return s.Length == 0 ? null : s;
    }

    /// <summary>The program at the start of a command line ("\"C:\a b\x.exe\" --arg" → C:\a b\x.exe).</summary>
    public static string? CommandExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var s = command.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }

        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? s[..(exe + 4)] : s.Split(' ')[0];
    }

    /// <summary>
    /// Games from other launchers, found in the installed apps list: Riot (VALORANT, League …), Battle.net, Ubisoft
    /// Connect, the EA app, Rockstar and Minecraft. Launchers themselves and Steam/Epic/GOG entries (read elsewhere)
    /// give null. <paramref name="exists"/> checks files (the installed list may point to deleted games).
    /// </summary>
    public static InstalledGame? FromUninstall(UninstallEntry e, Func<string, bool> exists, string? battleNetExe = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(exists);
        var name = e.Name?.Trim();
        if (string.IsNullOrEmpty(name) || LauncherNames.Contains(name))
        {
            return null;
        }

        var dir = e.InstallLocation?.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\') ?? "";
        var icon = IconPath(e.DisplayIcon);
        var publisher = e.Publisher ?? "";

        // Riot: key "Riot Game valorant.live", started through the Riot Client.
        if (e.Key.StartsWith("Riot Game ", StringComparison.Ordinal))
        {
            var product = e.Key["Riot Game ".Length..];
            var dot = product.IndexOf('.');
            var client = CommandExe(e.UninstallString);
            if (dot <= 0 || client is null || !exists(client))
            {
                return null;
            }

            return new InstalledGame(GameSources.Riot, product, name, dir, client, Image: icon,
                LaunchArgs: $"--launch-product={product[..dot]} --launch-patchline={product[(dot + 1)..]}");
        }

        // Ubisoft Connect: key "Uplay Install 1234".
        if (e.Key.StartsWith("Uplay Install ", StringComparison.Ordinal))
        {
            var id = e.Key["Uplay Install ".Length..].Trim();
            return dir.Length > 0 && exists(dir)
                ? new InstalledGame(GameSources.Ubisoft, id, name, dir, $"uplay://launch/{id}/0", Image: icon)
                : null;
        }

        if (publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase))
        {
            var uid = Arg(e.UninstallString, "--uid=");
            var exe = icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? icon : null;
            if (dir.Length == 0 || !exists(dir))
            {
                return null;
            }

            // Battle.net starts (and updates) its games itself: "Battle.net.exe --exec="launch Pro"" (a battlenet:// link
            // only opens the launcher). Without the launcher's path, the link; without a known code, the game's exe.
            if (uid is not null && BlizzardCodes.TryGetValue(uid, out var code))
            {
                return battleNetExe is not null && exists(battleNetExe)
                    ? new InstalledGame(GameSources.Blizzard, uid, name, dir, battleNetExe, Image: icon, LaunchArgs: $"--exec=\"launch {code}\"")
                    : new InstalledGame(GameSources.Blizzard, uid, name, dir, "battlenet://" + code, Image: icon);
            }

            return exe is null ? null : new InstalledGame(GameSources.Blizzard, uid ?? name, name, dir, exe, Path.GetDirectoryName(exe), icon);
        }

        if (publisher.Contains("Mojang", StringComparison.OrdinalIgnoreCase) && name.Contains("Minecraft", StringComparison.OrdinalIgnoreCase))
        {
            var exe = Path.Combine(dir, "MinecraftLauncher.exe");
            return exists(exe) ? new InstalledGame(GameSources.Minecraft, "minecraft", "Minecraft", dir, exe, dir, exe) : null;
        }

        var source = publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) ? GameSources.Ea
            : publisher.Contains("Rockstar Games", StringComparison.OrdinalIgnoreCase) ? GameSources.Rockstar
            : null;
        if (source is not null && icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exists(icon) && dir.Length > 0)
        {
            return new InstalledGame(source, e.Key, name, dir, icon, dir, icon);
        }

        return null;
    }

    /// <summary>Battle.net.exe from the installed apps list (its own entry), or null.</summary>
    public static string? BattleNetExe(IEnumerable<UninstallEntry> entries)
    {
        var bnet = entries.FirstOrDefault(e => string.Equals(e.Name?.Trim(), "Battle.net", StringComparison.OrdinalIgnoreCase)
                                               && (e.Publisher ?? "").Contains("Blizzard", StringComparison.OrdinalIgnoreCase));
        if (bnet is null)
        {
            return null;
        }

        if (IconPath(bnet.DisplayIcon) is { } icon && icon.EndsWith("Battle.net.exe", StringComparison.OrdinalIgnoreCase))
        {
            return icon;
        }

        var dir = bnet.InstallLocation?.Trim().Trim('"');
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "Battle.net.exe");
    }

    private static string? Arg(string? command, string prefix)
    {
        if (command is null)
        {
            return null;
        }

        var i = command.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return null;
        }

        var rest = command[(i + prefix.Length)..].TrimStart('"');
        var end = rest.IndexOfAny([' ', '"']);
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>
    /// The best cover among the files Steam caches for an app (paths relative to its cache folder; newer Steam keeps
    /// localised copies in sub-folders): the wide header first, then the hero picture. Null when there is none.
    /// </summary>
    public static string? PickSteamImage(IEnumerable<string> files)
    {
        static int Rank(string file)
        {
            var name = Path.GetFileName(file).ToLowerInvariant();
            if (!name.EndsWith(".jpg", StringComparison.Ordinal) && !name.EndsWith(".png", StringComparison.Ordinal))
            {
                return -1;
            }

            return name switch
            {
                "header.jpg" => 6,
                _ when name.EndsWith("_header.jpg", StringComparison.Ordinal) && !name.StartsWith("library", StringComparison.Ordinal) => 5,
                _ when name.StartsWith("library_header", StringComparison.Ordinal) => 4,
                "library_hero.jpg" => 3,
                _ when name.StartsWith("library_hero", StringComparison.Ordinal) && !name.Contains("blur", StringComparison.Ordinal) => 2,
                _ => -1,
            };
        }

        return files.Select(f => (File: f, Rank: Rank(f))).Where(x => x.Rank > 0).OrderByDescending(x => x.Rank).Select(x => x.File).FirstOrDefault();
    }

    /// <summary>Cover images Steam caches for an app, best first (newer and older cache layouts).</summary>
    public static IEnumerable<string> SteamImageCandidates(string steamPath, string appId)
    {
        var cache = Path.Combine(steamPath, "appcache", "librarycache");
        yield return Path.Combine(cache, appId, "header.jpg");
        yield return Path.Combine(cache, appId + "_header.jpg");
        yield return Path.Combine(cache, appId, "library_hero.jpg");
        yield return Path.Combine(cache, appId + "_library_hero.jpg");
    }

    /// <summary>One Epic launcher manifest (*.item, JSON) → a game (null for engines, plugins and tools).</summary>
    public static InstalledGame? EpicGame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var name = Str(r, "DisplayName");
            var app = Str(r, "AppName");
            var ns = Str(r, "CatalogNamespace");
            var item = Str(r, "CatalogItemId");
            var dir = Str(r, "InstallLocation");
            var exe = Str(r, "LaunchExecutable");
            var isApp = r.TryGetProperty("bIsApplication", out var a) && a.ValueKind == JsonValueKind.True;
            var isGame = r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                         cats.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "games");
            if (!isApp || !isGame || name.Length == 0 || app.Length == 0 || dir.Length == 0 || exe.Length == 0 || ns == "ue")
            {
                return null;
            }

            var launch = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(item)}%3A{Uri.EscapeDataString(app)}?action=launch&silent=true";
            return new InstalledGame(GameSources.Epic, app, name, dir, launch, Image: Path.Combine(dir, exe.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A GOG Galaxy / offline installer registry entry → a game (started through its own exe).</summary>
    public static InstalledGame? GogGame(string id, string? name, string? path, string? exe, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        return new InstalledGame(GameSources.Gog, id, name.Trim(), path, exe, string.IsNullOrWhiteSpace(workingDir) ? path : workingDir, Image: exe);
    }

    /// <summary>The installed game whose folder holds this exe (the longest matching folder wins).</summary>
    public static InstalledGame? ForExe(IEnumerable<InstalledGame> games, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        var exe = exePath.Replace('/', '\\');
        return games
            .Where(g => g.InstallDir.Length > 3 && exe.StartsWith(g.InstallDir.Replace('/', '\\').TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.InstallDir.Length)
            .FirstOrDefault();
    }

    /// <summary>Same game from two places (e.g. listed twice): one entry per name, most recently played first.</summary>
    public static List<InstalledGame> Merge(IEnumerable<InstalledGame> games) =>
        games
            .GroupBy(g => g.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.LastPlayed ?? DateTimeOffset.MinValue).First())
            .ToList();

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
