using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NotchPC.Core.Twitch;

/// <summary>A piece of a chat message: plain text or an emote (image from Twitch's CDN).</summary>
public sealed record ChatPart(string Text, string? EmoteId = null)
{
    public bool IsEmote => EmoteId is not null;

    /// <summary>Twitch emote CDN (dark theme, 1× ≈ 28 px).</summary>
    public string? EmoteUrl => EmoteId is null ? null : $"https://static-cdn.jtvnw.net/emoticons/v2/{EmoteId}/default/dark/1.0";
}

/// <summary>One chat line.</summary>
public sealed record ChatMessage(string Id, string User, string? Color, IReadOnlyList<ChatPart> Parts, bool IsAction, DateTimeOffset Time)
{
    /// <summary>Lower-case login (for timeouts / bans, which name the login).</summary>
    public string Login { get; init; } = "";

    public string Text => string.Concat(Parts.Select(p => p.Text));
}

/// <summary>What a line from Twitch's IRC means for the widget.</summary>
public abstract record IrcEvent
{
    public sealed record Message(ChatMessage Chat) : IrcEvent;

    /// <summary>A moderator deleted one message.</summary>
    public sealed record Deleted(string MessageId) : IrcEvent;

    /// <summary>A user was timed out / banned: their messages go away. Null user = the whole chat was cleared.</summary>
    public sealed record Cleared(string? User) : IrcEvent;

    public sealed record Ping(string Payload) : IrcEvent;

    public sealed record Joined : IrcEvent;

    /// <summary>The server asks to reconnect (maintenance).</summary>
    public sealed record Reconnect : IrcEvent;

    public sealed record Notice(string Text) : IrcEvent;
}

/// <summary>
/// Anonymous, read-only Twitch chat (IRC over WebSocket, nick "justinfanNNNN": no account, no token). Pure parsing.
/// </summary>
public static partial class TwitchIrc
{
    public const string Url = "wss://irc-ws.chat.twitch.tv:443";

    /// <summary>The lines to send after connecting.</summary>
    public static IReadOnlyList<string> Login(string channel, int nickNumber) =>
    [
        "CAP REQ :twitch.tv/tags twitch.tv/commands",
        "PASS SCHMOOPIIE",
        $"NICK justinfan{nickNumber}",
        $"JOIN #{channel}",
    ];

    /// <summary>"Some_Streamer" / "twitch.tv/some_streamer" / "#x" → "some_streamer"; null when not a valid name.</summary>
    public static string? NormalizeChannel(string? input)
    {
        var s = (input ?? "").Trim().TrimEnd('/');
        var slash = s.LastIndexOf('/');
        if (slash >= 0)
        {
            s = s[(slash + 1)..];
        }

        s = s.TrimStart('#').ToLowerInvariant();
        return ChannelName().IsMatch(s) ? s : null;
    }

    public static IrcEvent? Parse(string line, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var rest = line;
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rest.StartsWith('@'))
        {
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                return null;
            }

            foreach (var pair in rest[1..space].Split(';'))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    tags[pair[..eq]] = Unescape(pair[(eq + 1)..]);
                }
            }

            rest = rest[(space + 1)..];
        }

        string? prefix = null;
        if (rest.StartsWith(':'))
        {
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                return null;
            }

            prefix = rest[1..space];
            rest = rest[(space + 1)..];
        }

        var trailingAt = rest.IndexOf(" :", StringComparison.Ordinal);
        var trailing = trailingAt >= 0 ? rest[(trailingAt + 2)..] : null;
        var head = (trailingAt >= 0 ? rest[..trailingAt] : rest).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (head.Length == 0)
        {
            return null;
        }

        switch (head[0])
        {
            case "PING":
                return new IrcEvent.Ping(trailing ?? (head.Length > 1 ? head[1] : "tmi.twitch.tv"));
            case "RECONNECT":
                return new IrcEvent.Reconnect();
            case "366": // end of NAMES: joined
                return new IrcEvent.Joined();
            case "CLEARMSG":
                return tags.TryGetValue("target-msg-id", out var id) ? new IrcEvent.Deleted(id) : null;
            case "CLEARCHAT":
                return new IrcEvent.Cleared(string.IsNullOrEmpty(trailing) ? null : trailing.ToLowerInvariant());
            case "NOTICE":
                return trailing is null ? null : new IrcEvent.Notice(trailing);
            case "PRIVMSG" when trailing is not null:
                return new IrcEvent.Message(Message(tags, prefix, trailing, now));
            default:
                return null;
        }
    }

    /// <summary>
    /// Splits the text into plain and emote parts. Twitch gives emote positions in Unicode code points (not UTF-16),
    /// e.g. "emotes=25:0-4,12-16/1902:6-10".
    /// </summary>
    public static IReadOnlyList<ChatPart> Split(string text, string? emotes)
    {
        var ranges = new List<(int Start, int End, string Id)>();
        foreach (var group in (emotes ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = group.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var emoteId = group[..colon];
            foreach (var range in group[(colon + 1)..].Split(','))
            {
                var dash = range.IndexOf('-', StringComparison.Ordinal);
                if (dash > 0 && int.TryParse(range[..dash], CultureInfo.InvariantCulture, out var a)
                             && int.TryParse(range[(dash + 1)..], CultureInfo.InvariantCulture, out var b) && b >= a)
                {
                    ranges.Add((a, b, emoteId));
                }
            }
        }

        var runes = text.EnumerateRunes().ToArray();
        var parts = new List<ChatPart>();
        var plain = new StringBuilder();
        var position = 0;
        foreach (var (start, end, emoteId) in ranges.OrderBy(r => r.Start))
        {
            if (start < position || end >= runes.Length)
            {
                continue; // overlapping or out of range: keep as text
            }

            for (; position < start; position++)
            {
                plain.Append(runes[position].ToString());
            }

            if (plain.Length > 0)
            {
                parts.Add(new ChatPart(plain.ToString()));
                plain.Clear();
            }

            var name = new StringBuilder();
            for (; position <= end; position++)
            {
                name.Append(runes[position].ToString());
            }

            parts.Add(new ChatPart(name.ToString(), emoteId));
        }

        for (; position < runes.Length; position++)
        {
            plain.Append(runes[position].ToString());
        }

        if (plain.Length > 0)
        {
            parts.Add(new ChatPart(plain.ToString()));
        }

        return parts;
    }

    private static ChatMessage Message(Dictionary<string, string> tags, string? prefix, string text, DateTimeOffset now)
    {
        var action = text.StartsWith("\u0001ACTION ", StringComparison.Ordinal) && text.EndsWith('\u0001');
        if (action)
        {
            text = text[8..^1];
        }

        var login = prefix is null ? "" : prefix.Split('!')[0];
        var user = tags.TryGetValue("display-name", out var display) && display.Length > 0 ? display : login;
        var color = tags.TryGetValue("color", out var c) && HexColor().IsMatch(c) ? c : null;
        var id = tags.TryGetValue("id", out var messageId) ? messageId : Guid.NewGuid().ToString("N");
        return new ChatMessage(id, user, color, Split(text, tags.GetValueOrDefault("emotes")), action, now) { Login = login.ToLowerInvariant() };
    }

    /// <summary>IRCv3 tag value escaping.</summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }

            i++;
            sb.Append(value[i] switch
            {
                's' => ' ',
                ':' => ';',
                'r' => '\r',
                'n' => '\n',
                _ => value[i],
            });
        }

        return sb.ToString();
    }

    [GeneratedRegex("^[a-z0-9_]{3,25}$")]
    private static partial Regex ChannelName();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
