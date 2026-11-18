namespace NotchPC.Core.Notifications;

/// <summary>The notification sounds NotchPC can play (synthesised, no sound files shipped).</summary>
public static class NotificationSounds
{
    public const string None = "none";
    public const string Chime = "chime";
    public const string Drop = "drop";
    public const string Glass = "glass";

    public static IReadOnlyList<string> All { get; } = [Chime, Drop, Glass, None];

    public static string Normalize(string? id) => All.Contains(id ?? "") ? id! : Chime;
}

/// <summary>
/// Builds short, soft notification sounds as 16-bit mono WAV bytes: sine tones with a fast attack, a gentle
/// exponential decay and faint overtones (a small bell / marimba feel), so they are pleasant even when repeated.
/// </summary>
public static class ChimeSynth
{
    public const int SampleRate = 44100;
    private const double Peak = 0.55; // of full scale before the volume, leaves headroom

    private readonly record struct Note(double StartSeconds, double Frequency, double EndFrequency, double Decay, double Level);

    /// <summary>WAV file bytes for the sound, or null for <see cref="NotificationSounds.None"/> / volume 0.</summary>
    public static byte[]? Build(string sound, int volumePercent)
    {
        var volume = Math.Clamp(volumePercent, 0, 100) / 100.0;
        var notes = NotesFor(NotificationSounds.Normalize(sound));
        if (notes.Length == 0 || volume <= 0)
        {
            return null;
        }

        var length = notes.Max(n => n.StartSeconds + (n.Decay * 7));
        var samples = Render(notes, (int)Math.Ceiling(length * SampleRate));
        return ToWav(samples, volume * volume); // squared: the slider feels linear to the ear
    }

    private static Note[] NotesFor(string sound) => sound switch
    {
        // Two rising notes a fifth apart (A5 → E6): the classic "you have a message".
        NotificationSounds.Chime =>
        [
            new(0.000, 880.00, 880.00, 0.16, 0.85),
            new(0.095, 1318.51, 1318.51, 0.26, 1.00),
        ],

        // A quick upward "bloop", like a drop of water.
        NotificationSounds.Drop =>
        [
            new(0.000, 520, 1040, 0.09, 1.00),
        ],

        // Three light, high notes (E6 G#6 B6), a small glass arpeggio.
        NotificationSounds.Glass =>
        [
            new(0.000, 1318.51, 1318.51, 0.12, 0.70),
            new(0.070, 1661.22, 1661.22, 0.12, 0.70),
            new(0.140, 1975.53, 1975.53, 0.24, 0.85),
        ],

        _ => [],
    };

    private static double[] Render(Note[] notes, int count)
    {
        var buffer = new double[count];
        foreach (var note in notes)
        {
            var start = (int)(note.StartSeconds * SampleRate);
            var phase = 0.0;
            for (var i = start; i < count; i++)
            {
                var t = (double)(i - start) / SampleRate;

                // Pitch glide (only the drop uses it): settles within ~60 ms.
                var glide = 1 - Math.Exp(-t / 0.02);
                var frequency = note.Frequency + ((note.EndFrequency - note.Frequency) * glide);
                phase += 2 * Math.PI * frequency / SampleRate;

                var attack = 1 - Math.Exp(-t / 0.003); // 3 ms: soft, no click
                var body = Math.Exp(-t / note.Decay);
                var tone = Math.Sin(phase)
                    + (0.22 * Math.Sin(2 * phase) * Math.Exp(-t / (note.Decay * 0.5)))
                    + (0.06 * Math.Sin(3 * phase) * Math.Exp(-t / (note.Decay * 0.3)));
                buffer[i] += note.Level * attack * body * tone;
            }
        }

        // Normalise, then fade the last 20 ms so the tail never ends in a click.
        var max = buffer.Length == 0 ? 0 : buffer.Max(Math.Abs);
        var scale = max > 0 ? Peak / max : 0;
        var fade = Math.Min(count, SampleRate / 50);
        for (var i = 0; i < count; i++)
        {
            var tail = count - i <= fade ? (double)(count - i) / fade : 1;
            buffer[i] *= scale * tail;
        }

        return buffer;
    }

    private static byte[] ToWav(double[] samples, double volume)
    {
        const int headerSize = 44;
        var dataSize = samples.Length * 2;
        var wav = new byte[headerSize + dataSize];
        using var stream = new MemoryStream(wav);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(headerSize - 8 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                 // PCM chunk size
        writer.Write((short)1);           // PCM
        writer.Write((short)1);           // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);     // byte rate
        writer.Write((short)2);           // block align
        writer.Write((short)16);          // bits per sample
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (var s in samples)
        {
            writer.Write((short)Math.Round(Math.Clamp(s * volume, -1, 1) * short.MaxValue));
        }

        return wav;
    }
}

/// <summary>Banner glow colours: the Windows accent colour or a few fixed ones.</summary>
public static class GlowColors
{
    public const string Accent = "accent";

    public static IReadOnlyList<string> All { get; } = [Accent, "#5AC8FA", "#BF5AF2", "#FF375F", "#FF9F0A", "#30D158", "#FFFFFF"];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase)) ?? Accent;
}
