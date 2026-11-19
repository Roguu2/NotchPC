using System.Globalization;
using NotchPC.Core.Widgets;

namespace NotchPC.Tests;

public sealed class SystemInfoTests
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    [Theory]
    [InlineData(999_653_638_144L, "931 GB")]
    [InlineData(2_000_398_934_016L, "1,8 TB")]
    [InlineData(536_870_912L, "512 MB")]
    [InlineData(0L, "0 KB")]
    public void Sizes(long bytes, string text) => Assert.Equal(text, SystemInfoFormat.Size(bytes, Pl));

    [Theory]
    [InlineData(3 * 24 * 60 + 4 * 60 + 5, "3 d 4 h")]
    [InlineData(5 * 60 + 12, "5 h 12 min")]
    [InlineData(8, "8 min")]
    public void Uptime(int minutes, string text) => Assert.Equal(text, SystemInfoFormat.Uptime(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData("Microsoft Windows 10.0.26300", "Windows 11 · 26300")]
    [InlineData("Microsoft Windows 10.0.19045", "Windows 10 · 19045")]
    [InlineData("Something else", "Something else")]
    public void Windows_version(string os, string text) => Assert.Equal(text, SystemInfoFormat.Windows(os));

    [Fact]
    public void Used_fraction()
    {
        Assert.Equal(0.75, SystemInfoFormat.Used(400, 100), 6);
        Assert.Equal(0, SystemInfoFormat.Used(0, 0));
    }
}
