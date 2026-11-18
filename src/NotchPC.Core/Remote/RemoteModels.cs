using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NotchPC.Core.Remote;

/// <summary>Tokens and pairing codes: random, URL-safe; only SHA-256 hashes of device tokens are stored.</summary>
public static class RemoteSecrets
{
    /// <summary>256 random bits, base64url (no padding).</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>A pairing code: 128 random bits (guessing it is hopeless even without rate limits).</summary>
    public static string NewPairingCode() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    /// <summary>Constant-time comparison of a token with a stored hash.</summary>
    public static bool Matches(string token, string storedHash)
    {
        var a = Encoding.ASCII.GetBytes(Hash(token));
        var b = Encoding.ASCII.GetBytes(storedHash ?? "");
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The one pairing code currently shown as a QR code. Valid for a few minutes and only once: whoever scans it
/// first (on the user's own screen) becomes a paired device.
/// </summary>
public sealed class PairingCode(TimeSpan lifetime)
{
    private string? _code;
    private DateTimeOffset _expires;

    public string Current(DateTimeOffset now)
    {
        if (_code is null || now >= _expires)
        {
            _code = RemoteSecrets.NewPairingCode();
            _expires = now + lifetime;
        }

        return _code;
    }

    public DateTimeOffset Expires => _expires;

    /// <summary>True once for the valid code; the code is then used up.</summary>
    public bool TryUse(string? code, DateTimeOffset now)
    {
        if (_code is null || now >= _expires || code is null)
        {
            return false;
        }

        var ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(_code));
        if (ok)
        {
            _code = null;
        }

        return ok;
    }

    public void Invalidate() => _code = null;
}

public static class LocalNetwork
{
    /// <summary>
    /// Only the home network may talk to the remote: loopback, private IPv4 ranges, link-local and unique-local
    /// IPv6. Anything routed from the internet (e.g. a forwarded port) is refused.
    /// </summary>
    public static bool IsLocal(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC; // fc00::/7
        }

        return false;
    }

    /// <summary>"Android · Chrome", "iPhone · Safari" from a User-Agent (for the device list only).</summary>
    public static string DeviceName(string? userAgent)
    {
        var ua = userAgent ?? "";
        var device = ua.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
            : ua.Contains("iPad", StringComparison.Ordinal) ? "iPad"
            : ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("Mac OS", StringComparison.Ordinal) ? "Mac"
            : "Urządzenie";
        var browser = ua.Contains("SamsungBrowser", StringComparison.Ordinal) ? "Samsung Internet"
            : ua.Contains("Edg", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("Firefox", StringComparison.Ordinal) || ua.Contains("FxiOS", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("Chrome", StringComparison.Ordinal) || ua.Contains("CriOS", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari", StringComparison.Ordinal) ? "Safari"
            : null;
        return browser is null ? device : $"{device} · {browser}";
    }
}

/// <summary>A message from the phone (one JSON object per WebSocket message).</summary>
public abstract record RemoteCommand
{
    private const int MaxText = 500;
    private const int MaxDelta = 2000;

    /// <summary>Null for anything malformed or unknown (ignored by the server).</summary>
    public static RemoteCommand? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return t.GetString() switch
            {
                "auth" => Str(r, "token") is { } token ? new Auth(token) : null,
                "move" => new Move(Int(r, "dx", MaxDelta), Int(r, "dy", MaxDelta)),
                "scroll" => new Scroll(Int(r, "dx", MaxDelta), Int(r, "dy", MaxDelta)),
                "click" => ButtonOf(r) is { } b ? new Click(b, r.TryGetProperty("double", out var d) && d.ValueKind == JsonValueKind.True) : null,
                "down" => ButtonOf(r) is { } b2 ? new Press(b2, true) : null,
                "up" => ButtonOf(r) is { } b3 ? new Press(b3, false) : null,
                "text" => Str(r, "s") is { Length: > 0 and <= MaxText } s ? new Text(s) : null,
                "key" => Str(r, "k") is { } k && RemoteKeys.IsKnown(k) ? new Key(k) : null,
                "backspace" => new Backspace(Math.Clamp(Int(r, "n", 100), 1, 100)),
                "media" => Str(r, "a") is "playpause" or "next" or "prev" or "volup" or "voldown" or "mute" ? new Media(Str(r, "a")!) : null,
                "volume" => r.TryGetProperty("v", out var v) && v.TryGetDouble(out var vol) ? new Volume(Math.Clamp(vol, 0, 100)) : null,
                "launch" => r.TryGetProperty("i", out var i) && i.TryGetInt32(out var index) && index >= 0 ? new Launch(index) : null,
                "power" => Str(r, "a") is "lock" or "sleep" or "shutdown" or "restart" or "cancel" ? new Power(Str(r, "a")!) : null,
                "stats" => new Stats(r.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True),
                "overlay" => new ToggleOverlay(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int Int(JsonElement r, string name, int max) =>
        r.TryGetProperty(name, out var p) && p.TryGetDouble(out var d) && double.IsFinite(d) ? (int)Math.Clamp(Math.Round(d), -max, max) : 0;

    private static string? ButtonOf(JsonElement r) => Str(r, "b") is "left" or "right" or "middle" ? Str(r, "b") : null;

    public sealed record Auth(string Token) : RemoteCommand;

    public sealed record Move(int Dx, int Dy) : RemoteCommand;

    public sealed record Scroll(int Dx, int Dy) : RemoteCommand;

    public sealed record Click(string Button, bool IsDouble) : RemoteCommand;

    public sealed record Press(string Button, bool Down) : RemoteCommand;

    public sealed record Text(string Value) : RemoteCommand;

    public sealed record Key(string Name) : RemoteCommand;

    public sealed record Backspace(int Count) : RemoteCommand;

    public sealed record Media(string Action) : RemoteCommand;

    public sealed record Volume(double Percent) : RemoteCommand;

    public sealed record Launch(int Index) : RemoteCommand;

    public sealed record Power(string Action) : RemoteCommand;

    public sealed record Stats(bool On) : RemoteCommand;

    public sealed record ToggleOverlay : RemoteCommand;
}

/// <summary>Named keys and shortcuts the phone may send, as virtual-key sequences (held together, released in reverse).</summary>
public static class RemoteKeys
{
    private const ushort Ctrl = 0x11;
    private const ushort Alt = 0x12;
    private const ushort Shift = 0x10;
    private const ushort Win = 0x5B;

    private static readonly Dictionary<string, ushort[]> Map = new(StringComparer.Ordinal)
    {
        ["enter"] = [0x0D],
        ["esc"] = [0x1B],
        ["tab"] = [0x09],
        ["space"] = [0x20],
        ["backspace"] = [0x08],
        ["delete"] = [0x2E],
        ["left"] = [0x25],
        ["up"] = [0x26],
        ["right"] = [0x27],
        ["down"] = [0x28],
        ["home"] = [0x24],
        ["end"] = [0x23],
        ["pageup"] = [0x21],
        ["pagedown"] = [0x22],
        ["win"] = [Win],
        ["f5"] = [0x74],
        ["f11"] = [0x7A],
        ["b"] = [0x42],
        ["ctrl+c"] = [Ctrl, 0x43],
        ["ctrl+v"] = [Ctrl, 0x56],
        ["ctrl+x"] = [Ctrl, 0x58],
        ["ctrl+z"] = [Ctrl, 0x5A],
        ["ctrl+a"] = [Ctrl, 0x41],
        ["alt+tab"] = [Alt, 0x09],
        ["win+d"] = [Win, 0x44],
        ["shift+f5"] = [Shift, 0x74],
    };

    public static bool IsKnown(string name) => Map.ContainsKey(name);

    public static IReadOnlyList<ushort> Keys(string name) => Map.TryGetValue(name, out var keys) ? keys : [];
}
