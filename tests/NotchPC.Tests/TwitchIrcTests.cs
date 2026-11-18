using NotchPC.Core.Twitch;

namespace NotchPC.Tests;

public sealed class TwitchIrcTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 21, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Some_Streamer", "some_streamer")]
    [InlineData("https://www.twitch.tv/some_streamer/", "some_streamer")]
    [InlineData("#abc", "abc")]
    [InlineData("ab", null)]
    [InlineData("bad name", null)]
    [InlineData("", null)]
    public void Channel_names(string input, string? expected) => Assert.Equal(expected, TwitchIrc.NormalizeChannel(input));

    [Fact]
    public void Message_with_tags_and_emotes()
    {
        const string line = "@badge-info=;color=#1E90FF;display-name=Kappa_Fan;emotes=25:0-4,12-16;id=abc-1;mod=0 " +
                            ":kappa_fan!kappa_fan@kappa_fan.tmi.twitch.tv PRIVMSG #chan :Kappa hello Kappa";
        var e = Assert.IsType<IrcEvent.Message>(TwitchIrc.Parse(line, T0));
        var m = e.Chat;
        Assert.Equal("Kappa_Fan", m.User);
        Assert.Equal("kappa_fan", m.Login);
        Assert.Equal("#1E90FF", m.Color);
        Assert.Equal("abc-1", m.Id);
        Assert.Equal(["Kappa", " hello ", "Kappa"], m.Parts.Select(p => p.Text));
        Assert.Equal(["25", null, "25"], m.Parts.Select(p => p.EmoteId));
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/1.0", m.Parts[0].EmoteUrl);
        Assert.Equal("Kappa hello Kappa", m.Text);
    }

    [Fact]
    public void Emote_positions_count_code_points_not_utf16()
    {
        // "😀 Kappa": the emoji is 1 code point but 2 UTF-16 chars; Kappa is at 2-6.
        var parts = TwitchIrc.Split("😀 Kappa", "25:2-6");
        Assert.Equal(["😀 ", "Kappa"], parts.Select(p => p.Text));
        Assert.Equal("25", parts[1].EmoteId);
    }

    [Fact]
    public void Bad_emote_ranges_stay_text()
    {
        Assert.Equal("hi", Assert.Single(TwitchIrc.Split("hi", "25:5-9")).Text);
        Assert.Equal("hi", Assert.Single(TwitchIrc.Split("hi", "garbage")).Text);
    }

    [Fact]
    public void Action_message()
    {
        var e = Assert.IsType<IrcEvent.Message>(TwitchIrc.Parse(":a!a@a.tmi.twitch.tv PRIVMSG #c :\u0001ACTION waves\u0001", T0));
        Assert.True(e.Chat.IsAction);
        Assert.Equal("waves", e.Chat.Text);
        Assert.Equal("a", e.Chat.User);
        Assert.Null(e.Chat.Color);
    }

    [Fact]
    public void Escaped_tag_values()
    {
        var e = Assert.IsType<IrcEvent.Message>(TwitchIrc.Parse(@"@display-name=A\sB\:C;id=1 :x!x@x PRIVMSG #c :t", T0));
        Assert.Equal("A B;C", e.Chat.User);
    }

    [Fact]
    public void Control_lines()
    {
        Assert.Equal(new IrcEvent.Ping("tmi.twitch.tv"), TwitchIrc.Parse("PING :tmi.twitch.tv", T0));
        Assert.IsType<IrcEvent.Joined>(TwitchIrc.Parse(":justinfan1.tmi.twitch.tv 366 justinfan1 #c :End of /NAMES list", T0));
        Assert.IsType<IrcEvent.Reconnect>(TwitchIrc.Parse(":tmi.twitch.tv RECONNECT", T0));
        Assert.Equal(new IrcEvent.Deleted("m-1"), TwitchIrc.Parse("@login=a;target-msg-id=m-1 :tmi.twitch.tv CLEARMSG #c :bad", T0));
        Assert.Equal(new IrcEvent.Cleared("baduser"), TwitchIrc.Parse("@ban-duration=600 :tmi.twitch.tv CLEARCHAT #c :BadUser", T0));
        Assert.Equal(new IrcEvent.Cleared(null), TwitchIrc.Parse(":tmi.twitch.tv CLEARCHAT #c", T0));
        Assert.Null(TwitchIrc.Parse(":tmi.twitch.tv 001 justinfan1 :Welcome, GLHF!", T0));
        Assert.Null(TwitchIrc.Parse("", T0));
    }

    [Fact]
    public void Login_is_anonymous()
    {
        var lines = TwitchIrc.Login("chan", 12345);
        Assert.Contains("NICK justinfan12345", lines);
        Assert.Contains("JOIN #chan", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("PASS oauth", StringComparison.Ordinal));
    }
}
