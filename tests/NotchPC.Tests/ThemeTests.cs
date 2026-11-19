using NotchPC.Core.Settings;

namespace NotchPC.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void Default_theme_keeps_the_original_colours()
    {
        var p = ThemePalette.For(new ThemeSettings(), 0xFFFFFF);
        Assert.Equal(0xF20B0B0Du, p["IslandBackground"]);
        Assert.Equal(0x1FFFFFFFu, p["IslandBorder"]);
        Assert.Equal(0xFFFFFFFFu, p["NotchAccent"]);
    }

    [Fact]
    public void Oled_is_pure_black_and_glass_is_see_through()
    {
        var oled = ThemePalette.For(new ThemeSettings { Style = ThemeStyles.Oled, Opacity = 100 }, 0xFFFFFF);
        Assert.Equal(0xFF000000u, oled["IslandBackground"]);

        var glass = ThemePalette.For(new ThemeSettings { Style = ThemeStyles.Glass, Opacity = 100 }, 0xFFFFFF);
        Assert.True(glass["IslandBackground"] >> 24 < 0xC0);
    }

    [Fact]
    public void Opacity_is_clamped()
    {
        var p = ThemePalette.For(new ThemeSettings { Opacity = 5 }, 0xFFFFFF);
        Assert.Equal((uint)Math.Round(0.60 * 255), p["IslandBackground"] >> 24);
    }

    [Theory]
    [InlineData("white", 0xFFFFFFu)]
    [InlineData("#BF5AF2", 0xBF5AF2u)]
    [InlineData("bogus", 0xFFFFFFu)]
    public void Accent_colours(string accent, uint rgb) => Assert.Equal(rgb, ThemePalette.ParseAccent(accent));

    [Fact]
    public void Windows_accent_is_resolved_by_the_app() => Assert.Null(ThemePalette.ParseAccent("accent"));

    [Fact]
    public void Corners()
    {
        Assert.Equal((16.0, 28.0, 26.0), ThemeCorners.Radii("round"));
        Assert.Equal((9.0, 16.0, 14.0), ThemeCorners.Radii("soft"));
        Assert.Equal((16.0, 28.0, 26.0), ThemeCorners.Radii("weird"));
    }
}
