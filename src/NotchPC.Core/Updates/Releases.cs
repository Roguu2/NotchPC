using System.Globalization;
using System.Text.Json;

namespace NotchPC.Core.Updates;

public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, IReadOnlyList<ReleaseAsset> Assets, string Notes)
{
    /// <summary>The installer of this release, if it has one.</summary>
    public ReleaseAsset? Setup => Assets.FirstOrDefault(a => a.Name == Releases.SetupName(Version));
}

/// <summary>
/// NotchPC's releases on GitHub (the same feed SpotiTube uses: the repository's "latest release"). Pure parsing and
/// checks; downloading is done by the app.
/// </summary>
public static class Releases
{
    public const string Repository = "Roguu2/NotchPC";
    public const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string LatestPage = "https://github.com/" + Repository + "/releases/latest";
    private const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    /// <summary>"v0.2.0" / "0.2.0" → 0.2.0 (three parts); null for anything else.</summary>
    public static Version? ParseVersion(string? tag)
    {
        var t = (tag ?? "").Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out var v) && v.Major >= 0 ? Normalize(v) : null;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    public static string SetupName(Version v) =>
        string.Create(CultureInfo.InvariantCulture, $"NotchPC-{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}-Setup.exe");

    /// <summary>Only files of this repository's releases are downloaded (never a link from elsewhere).</summary>
    public static bool IsTrustedDownload(string? url) =>
        url is not null && url.StartsWith(DownloadPrefix, StringComparison.Ordinal) && Uri.IsWellFormedUriString(url, UriKind.Absolute);

    /// <summary>The GitHub API's "latest release" JSON → release, or null when it is not one we understand.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || Str(r, "tag_name") is not { } tag || ParseVersion(tag) is not { } version
                || (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                || (r.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True))
            {
                return null;
            }

            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    if (Str(a, "name") is { } name && Str(a, "browser_download_url") is { } url && IsTrustedDownload(url))
                    {
                        var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                        var digest = Str(a, "digest");
                        var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToUpperInvariant() : null;
                        assets.Add(new ReleaseAsset(name, url, size, sha));
                    }
                }
            }

            return new ReleaseInfo(version, tag, Str(r, "html_url") ?? LatestPage, assets, Str(r, "body") ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
