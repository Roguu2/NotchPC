namespace NotchPC.Core.Web;

/// <summary>Turns what was typed into the web panel's address box into a web address (or a search).</summary>
public static class WebAddress
{
    public const string SearchPrefix = "https://www.google.com/search?q=";

    /// <summary>
    /// "youtube.com" → https://youtube.com, "http://x" stays, "localhost:3000" → http://localhost:3000, anything else
    /// (spaces, no dot) → a search. Only http/https: other schemes (file:, javascript:) become a search too.
    /// </summary>
    public static Uri? Resolve(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
        {
            return absolute;
        }

        var local = text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || IsPrivateIp(text);
        var looksLikeHost = !text.Contains(' ', StringComparison.Ordinal)
                            && (text.Contains('.', StringComparison.Ordinal) || local)
                            && (!text.Contains(':', StringComparison.Ordinal) || HasPort(text));
        if (looksLikeHost)
        {
            var scheme = local ? "http://" : "https://";
            if (Uri.TryCreate(scheme + text, UriKind.Absolute, out var guessed) && guessed.Host.Length > 0 && !guessed.Host.EndsWith('.'))
            {
                return guessed;
            }
        }

        return new Uri(SearchPrefix + Uri.EscapeDataString(text));
    }

    /// <summary>Only web addresses may be bookmarked / opened from settings.</summary>
    public static bool IsWeb(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>"youtube.com" for https://www.youtube.com/watch?v=… (for a tile without a name).</summary>
    public static string ShortName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return url;
        }

        var host = u.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    /// <summary>"host:8080" or "host:8080/path" (a colon only before a port number).</summary>
    private static bool HasPort(string text)
    {
        var hostPart = text.Split('/')[0];
        var colon = hostPart.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon == hostPart.LastIndexOf(':') && int.TryParse(hostPart.AsSpan(colon + 1), out var port) && port is > 0 and < 65536;
    }

    private static bool IsPrivateIp(string text)
    {
        var host = text.Split(':', '/')[0];
        return System.Net.IPAddress.TryParse(host, out var ip) && Remote.LocalNetwork.IsLocal(ip);
    }
}
