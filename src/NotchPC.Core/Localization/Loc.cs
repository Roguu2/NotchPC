using System.Globalization;
using System.Resources;

namespace NotchPC.Core.Localization;

/// <summary>
/// Access to translated strings (Localization/Strings*.resx). Adding a language = adding Strings.&lt;culture&gt;.resx
/// and listing the culture in <see cref="SupportedCultures"/>.
/// </summary>
public static class Loc
{
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("en");

    public static ResourceManager Resources { get; } =
        new("NotchPC.Core.Localization.Strings", typeof(Loc).Assembly);

    public static IReadOnlyList<CultureInfo> SupportedCultures { get; } =
    [
        CultureInfo.GetCultureInfo("en"),
        CultureInfo.GetCultureInfo("pl"),
    ];

    public static CultureInfo Culture => _culture;

    public static event EventHandler? CultureChanged;

    /// <summary>Resolves the setting value ("" = Windows UI language) to one of the supported cultures.</summary>
    public static CultureInfo Resolve(string? setting, CultureInfo? systemUiCulture = null)
    {
        var wanted = string.IsNullOrWhiteSpace(setting)
            ? (systemUiCulture ?? CultureInfo.InstalledUICulture)
            : TryGetCulture(setting) ?? CultureInfo.InvariantCulture;

        foreach (var c in SupportedCultures)
        {
            if (string.Equals(c.TwoLetterISOLanguageName, wanted.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        return SupportedCultures[0];
    }

    public static void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (Equals(_culture, culture))
        {
            return;
        }

        _culture = culture;
        CultureChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key) => Resources.GetString(key, _culture) ?? $"!{key}!";

    public static string Format(string key, params object?[] args) =>
        string.Format(_culture, Get(key), args);

    private static CultureInfo? TryGetCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
