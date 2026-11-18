using System.Net;
using NotchPC.Core.Remote;
using NotchPC.Core.Settings;

namespace NotchPC.Tests;

public sealed class RemoteTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tokens_are_random_and_only_match_their_hash()
    {
        var a = RemoteSecrets.NewToken();
        var b = RemoteSecrets.NewToken();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);

        var hash = RemoteSecrets.Hash(a);
        Assert.Equal(64, hash.Length);
        Assert.True(RemoteSecrets.Matches(a, hash));
        Assert.False(RemoteSecrets.Matches(b, hash));
        Assert.False(RemoteSecrets.Matches("", hash));
    }

    [Fact]
    public void Pairing_code_works_once()
    {
        var pairing = new PairingCode(TimeSpan.FromMinutes(10));
        var code = pairing.Current(T0);

        Assert.False(pairing.TryUse("wrong", T0));
        Assert.True(pairing.TryUse(code, T0.AddMinutes(1)));
        Assert.False(pairing.TryUse(code, T0.AddMinutes(1)));
        Assert.NotEqual(code, pairing.Current(T0.AddMinutes(1)));
    }

    [Fact]
    public void Pairing_code_expires_and_is_replaced()
    {
        var pairing = new PairingCode(TimeSpan.FromMinutes(10));
        var code = pairing.Current(T0);

        Assert.Equal(code, pairing.Current(T0.AddMinutes(9)));
        Assert.False(pairing.TryUse(code, T0.AddMinutes(10)));
        Assert.NotEqual(code, pairing.Current(T0.AddMinutes(10)));
    }

    [Fact]
    public void Invalidated_code_no_longer_works()
    {
        var pairing = new PairingCode(TimeSpan.FromMinutes(10));
        var code = pairing.Current(T0);
        pairing.Invalidate();
        Assert.False(pairing.TryUse(code, T0));
        Assert.False(pairing.TryUse(null, T0));
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:192.168.0.7", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_the_home_network_is_served(string ip, bool local) =>
        Assert.Equal(local, LocalNetwork.IsLocal(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36", "Android · Chrome")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "iPhone · Safari")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SM-S911B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/25.0 Chrome/121.0 Mobile Safari/537.36", "Android · Samsung Internet")]
    [InlineData(null, "Urządzenie")]
    public void Device_name_from_user_agent(string? userAgent, string expected) =>
        Assert.Equal(expected, LocalNetwork.DeviceName(userAgent));

    [Fact]
    public void Commands_are_parsed()
    {
        Assert.Equal(new RemoteCommand.Move(5, -3), RemoteCommand.Parse("""{"t":"move","dx":5.4,"dy":-3}"""));
        Assert.Equal(new RemoteCommand.Click("right", false), RemoteCommand.Parse("""{"t":"click","b":"right"}"""));
        Assert.Equal(new RemoteCommand.Text("zażółć"), RemoteCommand.Parse("""{"t":"text","s":"zażółć"}"""));
        Assert.Equal(new RemoteCommand.Key("ctrl+c"), RemoteCommand.Parse("""{"t":"key","k":"ctrl+c"}"""));
        Assert.Equal(new RemoteCommand.Launch(2), RemoteCommand.Parse("""{"t":"launch","i":2}"""));
        Assert.Equal(new RemoteCommand.Power("shutdown"), RemoteCommand.Parse("""{"t":"power","a":"shutdown"}"""));
        Assert.Equal(new RemoteCommand.Stats(true), RemoteCommand.Parse("""{"t":"stats","on":true}"""));
        Assert.Equal(new RemoteCommand.Auth("abc"), RemoteCommand.Parse("""{"t":"auth","token":"abc"}"""));
    }

    [Fact]
    public void Values_are_clamped()
    {
        Assert.Equal(new RemoteCommand.Move(2000, -2000), RemoteCommand.Parse("""{"t":"move","dx":1e9,"dy":-99999}"""));
        Assert.Equal(new RemoteCommand.Backspace(100), RemoteCommand.Parse("""{"t":"backspace","n":5000}"""));
        Assert.Equal(new RemoteCommand.Backspace(1), RemoteCommand.Parse("""{"t":"backspace","n":-4}"""));
        Assert.Equal(new RemoteCommand.Volume(100), RemoteCommand.Parse("""{"t":"volume","v":250}"""));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"t":"format-disk"}""")]
    [InlineData("""{"t":"key","k":"alt+f4"}""")]
    [InlineData("""{"t":"power","a":"hibernate"}""")]
    [InlineData("""{"t":"launch","i":-1}""")]
    [InlineData("""{"t":"click","b":"x"}""")]
    [InlineData("""{"t":"text","s":""}""")]
    [InlineData("""{"t":"auth"}""")]
    public void Unknown_or_malformed_is_ignored(string json) => Assert.Null(RemoteCommand.Parse(json));

    [Fact]
    public void Long_text_is_refused()
    {
        Assert.Null(RemoteCommand.Parse($$"""{"t":"text","s":"{{new string('a', 501)}}"}"""));
    }

    [Fact]
    public void Shortcut_keys_are_held_together()
    {
        Assert.Equal([0x11, 0x43], RemoteKeys.Keys("ctrl+c"));
        Assert.Equal([0x27], RemoteKeys.Keys("right"));
        Assert.Empty(RemoteKeys.Keys("nope"));
    }

    [Fact]
    public void Settings_store_cleans_remote_values()
    {
        var s = new AppSettings();
        s.Remote.Port = 80;
        s.Remote.PowerDelaySeconds = 9999;
        s.Remote.Devices =
        [
            new PairedDevice { Id = "a", Name = "x", TokenHash = new string('A', 64) },
            new PairedDevice { Id = "a", Name = "dup", TokenHash = new string('B', 64) },
            new PairedDevice { Id = "b", Name = "bad hash", TokenHash = "123" },
            new PairedDevice { Id = "", Name = "no id", TokenHash = new string('C', 64) },
        ];

        SettingsStore.Normalize(s);

        Assert.Equal(47800, s.Remote.Port);
        Assert.Equal(600, s.Remote.PowerDelaySeconds);
        Assert.Equal("x", Assert.Single(s.Remote.Devices).Name);
        Assert.False(s.Remote.Enabled);
    }
}
