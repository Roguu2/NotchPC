namespace NotchPC.Core.Music;

/// <summary>Matching a media session's AppUserModelID to a program by name (pure logic).</summary>
public static class MediaSources
{
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "com", "app", "exe", "net", "org", "userdata", "profile", "default", "microsoft", "windows", "desktop", "player",
    };

    /// <summary>"Brave.UserData.Profile2" ~ brave.exe, "com.spotitube.app" ~ SpotiTube.exe, "MSEdge" ~ msedge.exe.</summary>
    public static bool NameMatches(string appUserModelId, string exePath)
    {
        var exe = Path.GetFileNameWithoutExtension(exePath ?? "").ToLowerInvariant();
        if (exe.Length == 0 || string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        var tokens = appUserModelId.ToLowerInvariant()
            .Split(['.', '_', '!', '-', ' ', '\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !NoiseTokens.Contains(t));
        return tokens.Any(t => t == exe
                               || (t.Length >= 5 && exe.StartsWith(t, StringComparison.Ordinal))
                               || (exe.Length >= 5 && t.StartsWith(exe, StringComparison.Ordinal)));
    }
}
