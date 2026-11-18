using System.Buffers.Binary;
using System.Text;
using NotchPC.Core.Notifications;
using NotchPC.Core.Settings;

namespace NotchPC.Tests;

public sealed class NotificationSoundTests
{
    [Theory]
    [InlineData(NotificationSounds.Chime)]
    [InlineData(NotificationSounds.Drop)]
    [InlineData(NotificationSounds.Glass)]
    public void Builds_a_short_valid_mono_wav(string sound)
    {
        var wav = ChimeSynth.Build(sound, 60)!;

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));             // mono
        Assert.Equal(ChimeSynth.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        var dataSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        Assert.Equal(wav.Length - 44, dataSize);
        var seconds = dataSize / 2.0 / ChimeSynth.SampleRate;
        Assert.InRange(seconds, 0.3, 2.5);
    }

    [Fact]
    public void Starts_and_ends_silent_and_never_clips()
    {
        var wav = ChimeSynth.Build(NotificationSounds.Chime, 100)!;
        var samples = Enumerable.Range(0, (wav.Length - 44) / 2)
            .Select(i => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 2))))
            .ToArray();

        Assert.Equal(0, samples[0]);
        Assert.Equal(0, samples[^1]);
        Assert.True(samples.Max(s => Math.Abs((int)s)) < short.MaxValue * 0.6);
    }

    [Fact]
    public void Lower_volume_is_quieter()
    {
        static int PeakOf(byte[] wav) => Enumerable.Range(0, (wav.Length - 44) / 2)
            .Max(i => Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 2)))));

        Assert.True(PeakOf(ChimeSynth.Build(NotificationSounds.Drop, 30)!) < PeakOf(ChimeSynth.Build(NotificationSounds.Drop, 90)!));
    }

    [Fact]
    public void No_sound_for_none_or_zero_volume()
    {
        Assert.Null(ChimeSynth.Build(NotificationSounds.None, 80));
        Assert.Null(ChimeSynth.Build(NotificationSounds.Chime, 0));
    }

    [Theory]
    [InlineData("glass", "glass")]
    [InlineData("bogus", "chime")]
    [InlineData(null, "chime")]
    public void Unknown_sound_falls_back_to_chime(string? value, string expected) =>
        Assert.Equal(expected, NotificationSounds.Normalize(value));

    [Theory]
    [InlineData("#bf5af2", "#BF5AF2")]
    [InlineData("accent", "accent")]
    [InlineData("#123456", "accent")]
    [InlineData(null, "accent")]
    public void Glow_colour_is_one_of_the_choices(string? value, string expected) =>
        Assert.Equal(expected, GlowColors.Normalize(value));

    [Fact]
    public void Settings_store_cleans_sound_values()
    {
        var s = new AppSettings();
        s.Notifications.Sound = "loud";
        s.Notifications.SoundVolume = 400;
        s.Notifications.GlowColor = "red";

        SettingsStore.Normalize(s);

        Assert.Equal("chime", s.Notifications.Sound);
        Assert.Equal(100, s.Notifications.SoundVolume);
        Assert.Equal("accent", s.Notifications.GlowColor);
    }
}
