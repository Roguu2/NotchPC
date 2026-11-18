using System.Collections;
using System.Globalization;
using System.Resources;
using NotchPC.Core.Localization;
using NotchPC.Core.Settings;

namespace NotchPC.Tests;

public sealed class LocalizationTests
{
    private static Dictionary<string, string> Strings(CultureInfo culture)
    {
        var set = Loc.Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"No resources for {culture.Name}");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    [Fact]
    public void Every_supported_language_has_exactly_the_english_keys()
    {
        var english = Strings(CultureInfo.InvariantCulture);
        Assert.NotEmpty(english);

        foreach (var culture in Loc.SupportedCultures.Where(c => c.Name != "en"))
        {
            var other = Strings(culture);
            Assert.Empty(english.Keys.Except(other.Keys));
            Assert.Empty(other.Keys.Except(english.Keys));
            Assert.All(other, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{culture.Name}:{kv.Key} is empty"));
        }
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        var english = Strings(CultureInfo.InvariantCulture);
        foreach (var culture in Loc.SupportedCultures.Where(c => c.Name != "en"))
        {
            foreach (var (key, value) in Strings(culture))
            {
                Assert.Equal(Placeholders(english[key]), Placeholders(value));
            }
        }

        static string Placeholders(string s) =>
            string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Order());
    }

    [Fact]
    public void Every_tab_has_a_label()
    {
        var english = Strings(CultureInfo.InvariantCulture);
        Assert.All(TabIds.DefaultOrder, id => Assert.True(english.ContainsKey("Tab." + id), $"missing Tab.{id}"));
    }

    [Theory]
    [InlineData("", "pl-PL", "pl")]
    [InlineData("", "de-DE", "en")]
    [InlineData("en", "pl-PL", "en")]
    [InlineData("pl", "en-US", "pl")]
    [InlineData("xx-not-a-culture", "pl-PL", "en")]
    public void Resolve_picks_a_supported_culture(string setting, string system, string expected)
    {
        Assert.Equal(expected, Loc.Resolve(setting, CultureInfo.GetCultureInfo(system)).Name);
    }

    [Fact]
    public void Missing_key_is_visible_instead_of_crashing()
    {
        Assert.Equal("!No.Such.Key!", Loc.Get("No.Such.Key"));
    }
}
