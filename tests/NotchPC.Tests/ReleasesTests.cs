using NotchPC.Core.Updates;

namespace NotchPC.Tests;

public sealed class ReleasesTests
{
    private const string Json = """
        {
          "tag_name": "v0.2.0", "html_url": "https://github.com/Roguu2/NotchPC/releases/tag/v0.2.0",
          "draft": false, "prerelease": false, "body": "## What's new\n- x",
          "assets": [
            { "name": "NotchPC-0.2.0-Setup.exe", "size": 1234,
              "digest": "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
              "browser_download_url": "https://github.com/Roguu2/NotchPC/releases/download/v0.2.0/NotchPC-0.2.0-Setup.exe" },
            { "name": "NotchPC-0.2.0-win-x64-portable.zip", "size": 99,
              "browser_download_url": "https://github.com/Roguu2/NotchPC/releases/download/v0.2.0/NotchPC-0.2.0-win-x64-portable.zip" },
            { "name": "evil.exe", "size": 1, "browser_download_url": "https://example.com/evil.exe" }
          ]
        }
        """;

    [Fact]
    public void Latest_release_is_read()
    {
        var r = Releases.Parse(Json)!;
        Assert.Equal(new Version(0, 2, 0), r.Version);
        Assert.Equal("v0.2.0", r.Tag);
        Assert.Equal(2, r.Assets.Count); // the file from another site is ignored
        Assert.Equal("NotchPC-0.2.0-Setup.exe", r.Setup!.Name);
        Assert.Equal(1234, r.Setup.Size);
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", r.Setup.Sha256);
        Assert.Contains("What's new", r.Notes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.3.0","draft":true}""")]
    [InlineData("""{"tag_name":"v0.3.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"latest"}""")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("not json")]
    public void Drafts_prereleases_and_junk_are_ignored(string json) => Assert.Null(Releases.Parse(json));

    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("0.1.1", "0.1.0.0", true)]
    [InlineData("v0.1.0", "0.1.0.0", false)]
    [InlineData("v0.0.9", "0.1.0", false)]
    [InlineData("v1.0", "0.9.9", true)]
    public void Newer(string tag, string current, bool newer) =>
        Assert.Equal(newer, Releases.IsNewer(Releases.ParseVersion(tag)!, Version.Parse(current)));

    [Fact]
    public void Setup_file_name_matches_the_release_workflow() =>
        Assert.Equal("NotchPC-1.2.3-Setup.exe", Releases.SetupName(new Version(1, 2, 3)));

    [Theory]
    [InlineData("https://github.com/Roguu2/NotchPC/releases/download/v1/NotchPC-1.0.0-Setup.exe", true)]
    [InlineData("https://github.com/Someone/NotchPC/releases/download/v1/x.exe", false)]
    [InlineData("http://github.com/Roguu2/NotchPC/releases/download/v1/x.exe", false)]
    [InlineData(null, false)]
    public void Only_this_repository_is_trusted(string? url, bool trusted) =>
        Assert.Equal(trusted, Releases.IsTrustedDownload(url));
}
