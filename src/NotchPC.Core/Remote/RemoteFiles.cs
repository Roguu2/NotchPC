using System.Security.Cryptography;
using System.Text;

namespace NotchPC.Core.Remote;

/// <summary>Files sent between the phone and the PC: safe names for what arrives, ids for what the PC offers.</summary>
public static class RemoteFiles
{
    public const int MaxNameLength = 120;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A name the phone sent, made safe for a file in one folder: no folders, no characters Windows forbids,
    /// no reserved device names, no trailing dots/spaces, not too long. Empty input gives <paramref name="fallback"/>.
    /// </summary>
    public static string SafeName(string? raw, string fallback = "file")
    {
        var name = (raw ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(c < 32 || invalid.Contains(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c);
        }

        name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.All(c => c == '.'))
        {
            name = fallback;
        }

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (Reserved.Contains(stem))
        {
            stem = "_" + stem;
        }

        if (ext.Length > 20)
        {
            ext = "";
            stem = name;
        }

        if (stem.Length + ext.Length > MaxNameLength)
        {
            stem = stem[..Math.Max(1, MaxNameLength - ext.Length)].TrimEnd('.', ' ');
        }

        return stem + ext;
    }

    /// <summary>"photo.jpg", or "photo (2).jpg", "photo (3).jpg" … when the name is taken.</summary>
    public static string Unique(string name, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (!exists(name))
        {
            return name;
        }

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Stable id of a file the PC offers (the phone never sees or sends paths).</summary>
    public static string IdFor(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..20];

    /// <summary>"3,4 MB" style sizes for the phone list.</summary>
    public static string Size(long bytes, System.Globalization.CultureInfo culture)
    {
        double b = Math.Max(0, bytes);
        return b switch
        {
            >= 1024d * 1024 * 1024 => (b / (1024d * 1024 * 1024)).ToString("0.0", culture) + " GB",
            >= 1024d * 1024 => (b / (1024d * 1024)).ToString("0.0", culture) + " MB",
            >= 1024 => (b / 1024).ToString("0", culture) + " KB",
            _ => b.ToString("0", culture) + " B",
        };
    }
}
