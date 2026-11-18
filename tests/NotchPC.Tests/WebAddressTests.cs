using NotchPC.Core.Web;

namespace NotchPC.Tests;

public sealed class WebAddressTests
{
    [Theory]
    [InlineData("youtube.com", "https://youtube.com/")]
    [InlineData("https://example.org/a?b=1", "https://example.org/a?b=1")]
    [InlineData("http://example.org", "http://example.org/")]
    [InlineData("  pl.wikipedia.org/wiki/Notch ", "https://pl.wikipedia.org/wiki/Notch")]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("192.168.1.1", "http://192.168.1.1/")]
    [InlineData("example.com:8080/x", "https://example.com:8080/x")]
    public void Addresses_open_directly(string input, string expected) =>
        Assert.Equal(expected, WebAddress.Resolve(input)!.AbsoluteUri);

    [Theory]
    [InlineData("pogoda kraków")]
    [InlineData("notchpc")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("a:b:c")]
    public void Anything_else_is_a_search(string input) =>
        Assert.StartsWith(WebAddress.SearchPrefix, WebAddress.Resolve(input)!.AbsoluteUri, StringComparison.Ordinal);

    [Fact]
    public void Empty_is_nothing() => Assert.Null(WebAddress.Resolve("   "));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=1", "youtube.com")]
    [InlineData("https://chatgpt.com/", "chatgpt.com")]
    [InlineData("nonsense", "nonsense")]
    public void Short_name(string url, string expected) => Assert.Equal(expected, WebAddress.ShortName(url));

    [Theory]
    [InlineData("https://a.b", true)]
    [InlineData("http://a.b", true)]
    [InlineData("ftp://a.b", false)]
    [InlineData("C:\\x", false)]
    [InlineData(null, false)]
    public void Only_web_links(string? url, bool web) => Assert.Equal(web, WebAddress.IsWeb(url));
}
