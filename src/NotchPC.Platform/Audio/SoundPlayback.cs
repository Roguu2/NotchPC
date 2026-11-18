using System.Runtime.InteropServices;

namespace NotchPC.Platform.Audio;

/// <summary>
/// Plays a WAV from memory without blocking (winmm PlaySound). The sound goes to the default output at NotchPC's
/// own volume in the Windows mixer, so muting the system mutes it too. One sound at a time: a new one stops the last.
/// </summary>
public static partial class SoundPlayback
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private static readonly Lock Gate = new();
    private static IntPtr _buffer; // PlaySound reads it while playing asynchronously, so it must stay put

    public static void Play(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        lock (Gate)
        {
            _ = PlaySoundW(IntPtr.Zero, IntPtr.Zero, 0); // stop the previous one before freeing its memory
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
            }

            _buffer = Marshal.AllocHGlobal(wav.Length);
            Marshal.Copy(wav, 0, _buffer, wav.Length);
            _ = PlaySoundW(_buffer, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
        }
    }

    [LibraryImport("winmm.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(IntPtr sound, IntPtr module, uint flags);
}
