using NotchPC.Core.Music;

namespace NotchPC.Tests;

public sealed class MediaSourcesTests
{
    [Theory]
    [InlineData("Brave.UserData.Profile2", @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", true)]
    [InlineData("com.spotitube.app", @"C:\Users\x\AppData\Local\Programs\SpotiTube\SpotiTube.exe", true)]
    [InlineData("Spotify.exe", @"C:\Users\x\AppData\Roaming\Spotify\Spotify.exe", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", @"C:\Program Files\WindowsApps\x\Spotify.exe", true)]
    [InlineData("MSEdge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", true)]
    [InlineData("Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", true)]
    [InlineData("Brave.UserData.Profile2", @"C:\Discord\Discord.exe", false)]
    [InlineData("com.spotitube.app", @"C:\x\app.exe", false)]
    [InlineData("308046B0AF4A39CB", @"C:\Program Files\Mozilla Firefox\firefox.exe", false)]
    [InlineData("", @"C:\x\a.exe", false)]
    public void Player_id_matches_its_program(string id, string exe, bool match) =>
        Assert.Equal(match, MediaSources.NameMatches(id, exe));
}
