using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NotchPC.Platform.Audio;

/// <summary>
/// One app in the volume mixer: all audio sessions of the same program on the default output together (Discord or
/// a browser run several processes). <paramref name="Key"/> identifies it for <see cref="AudioSessions.SetVolume"/>.
/// </summary>
public sealed record AudioApp(string Key, string Name, string? ExePath, float Volume, bool Muted, bool IsSystemSounds, bool IsActive);

/// <summary>
/// The per-app volume mixer of the default output device (Core Audio IAudioSessionManager2, no admin rights).
/// Same data as Windows' own mixer; the volume is relative to the device volume.
/// </summary>
public static partial class AudioSessions
{
    public const string SystemKey = "system";

    private const uint ClsCtxAll = 23;
    private const int StateExpired = 2;
    private const int StateActive = 1;
    private static readonly Guid SessionManager2Id = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static Guid _context = Guid.NewGuid();

    /// <summary>Apps with an audio session on the default output: system sounds first, then playing ones.</summary>
    public static IReadOnlyList<AudioApp> List()
    {
        var apps = new Dictionary<string, AudioApp>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<uint, string?>();
        ForEach((control, volume) =>
        {
            if (control.GetState(out var state) != 0 || state == StateExpired || KeyOf(control, paths) is not { } key)
            {
                return;
            }

            volume.GetMasterVolume(out var level);
            volume.GetMute(out var muted);
            var active = state == StateActive;
            if (apps.TryGetValue(key, out var existing))
            {
                // Show the playing session's level when the program has several.
                apps[key] = active && !existing.IsActive ? existing with { Volume = level, Muted = muted, IsActive = true } : existing;
                return;
            }

            var system = key == SystemKey;
            control.GetProcessId(out var pid);
            var path = system ? null : paths.GetValueOrDefault(pid);
            var name = system ? "" : DisplayName(control, path, pid);
            if (!system && name.Length == 0)
            {
                return; // process already gone
            }

            apps[key] = new AudioApp(key, name, path, level, muted, system, active);
        });

        return [.. apps.Values.OrderByDescending(a => a.IsSystemSounds).ThenByDescending(a => a.IsActive).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Sets the volume (0–1) of every session of the app.</summary>
    public static void SetVolume(string key, float volume) =>
        ForApp(key, v => v.SetMasterVolume(Math.Clamp(volume, 0f, 1f), ref _context));

    public static void SetMute(string key, bool muted) =>
        ForApp(key, v => v.SetMute(muted, ref _context));

    /// <summary>
    /// The mixer entry of the app behind a media session (Windows' media controls name it by AppUserModelID, e.g.
    /// "Spotify.exe", "Brave.UserData.Profile2", "com.spotitube.app", "SpotifyAB.SpotifyMusic_…!Spotify"):
    /// its window, else its Start menu shortcut, else its name inside the ID. Null when nothing matches.
    /// </summary>
    public static AudioApp? FindForMediaSource(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }

        var apps = List().Where(a => !a.IsSystemSounds && a.ExePath is not null).ToList();
        if (apps.Count == 0)
        {
            return null;
        }

        AudioApp? Pick(Func<AudioApp, bool> match) =>
            apps.Where(match).OrderByDescending(a => a.IsActive).FirstOrDefault();

        // 1. A window that carries this ID (browsers per profile, packaged apps).
        var hwnd = Shell.AppActivator.FindWindow(appUserModelId);
        if (hwnd != IntPtr.Zero && Shell.WindowHelper.ProcessIdOf(hwnd) is var pid && pid != 0
            && Shell.AppActivator.ProcessPath(pid) is { } windowExe
            && Pick(a => string.Equals(a.ExePath, windowExe, StringComparison.OrdinalIgnoreCase)) is { } byWindow)
        {
            return byWindow;
        }

        // 2. The Start menu shortcut with this ID (Electron and other installed desktop apps).
        if (Shell.AppActivator.TargetOf(appUserModelId) is { } target)
        {
            var folder = Path.GetDirectoryName(target) ?? "";
            if (Pick(a => string.Equals(a.ExePath, target, StringComparison.OrdinalIgnoreCase)
                          || (folder.Length > 3 && a.ExePath!.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))) is { } byShortcut)
            {
                return byShortcut;
            }
        }

        // 3. The program's name inside the ID.
        return Pick(a => Core.Music.MediaSources.NameMatches(appUserModelId, a.ExePath!));
    }


    private static void ForApp(string key, Action<ISimpleAudioVolume> action)
    {
        var paths = new Dictionary<uint, string?>();
        ForEach((control, volume) =>
        {
            if (control.GetState(out var state) == 0 && state != StateExpired
                && string.Equals(KeyOf(control, paths), key, StringComparison.OrdinalIgnoreCase))
            {
                action(volume);
            }
        });
    }

    /// <summary>"system", the program's path, or "pid:N" when the path cannot be read; null when it failed.</summary>
    private static string? KeyOf(IAudioSessionControl2 control, Dictionary<uint, string?> paths)
    {
        if (control.IsSystemSoundsSession() == 0)
        {
            return SystemKey;
        }

        if (control.GetProcessId(out var pid) != 0)
        {
            return null;
        }

        if (!paths.TryGetValue(pid, out var path))
        {
            path = ProcessPath(pid);
            paths[pid] = path;
        }

        return path ?? $"pid:{pid}";
    }

    private static void ForEach(Action<IAudioSessionControl2, ISimpleAudioVolume> visit)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice? device = null;
        object? managerObject = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, DeviceRole.Multimedia, out device) != 0 || device is null)
            {
                return;
            }

            var iid = SessionManager2Id;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out managerObject) != 0 || managerObject is not IAudioSessionManager2 manager)
            {
                return;
            }

            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null)
            {
                return;
            }

            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var sessionObject) != 0 || sessionObject is null)
                {
                    continue;
                }

                try
                {
                    if (sessionObject is IAudioSessionControl2 control && sessionObject is ISimpleAudioVolume volume)
                    {
                        visit(control, volume);
                    }
                }
                catch (COMException)
                {
                    // The session ended while we looked at it.
                }
                finally
                {
                    Marshal.ReleaseComObject(sessionObject);
                }
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            Release(sessions);
            Release(managerObject);
            Release(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o))
        {
            Marshal.ReleaseComObject(o);
        }
    }

    /// <summary>The session's own name, else the exe's description ("Google Chrome"), else the process name.</summary>
    private static string DisplayName(IAudioSessionControl2 control, string? path, uint pid)
    {
        if (control.GetDisplayName(out var ptr) == 0 && ptr != IntPtr.Zero)
        {
            var own = Marshal.PtrToStringUni(ptr);
            Marshal.FreeCoTaskMem(ptr);
            if (!string.IsNullOrWhiteSpace(own) && !own.StartsWith('@'))
            {
                return own;
            }
        }

        if (path is not null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }
            }
            catch (FileNotFoundException)
            {
            }

            return Path.GetFileNameWithoutExtension(path);
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            return "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static unsafe string? ProcessPath(uint pid)
    {
        var process = OpenProcess(0x1000, 0, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return QueryFullProcessImageNameW(process, 0, buffer, &size) != 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(uint access, int inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int QueryFullProcessImageNameW(IntPtr process, uint flags, char* buffer, uint* size);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);

    [PreserveSig]
    int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName(out IntPtr name);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);

    [PreserveSig]
    int GetIconPath(out IntPtr path);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);

    [PreserveSig]
    int GetGroupingParam(out Guid grouping);

    [PreserveSig]
    int SetGroupingParam(ref Guid grouping, ref Guid context);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr events);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr events);

    // IAudioSessionControl2
    [PreserveSig]
    int GetSessionIdentifier(out IntPtr id);

    [PreserveSig]
    int GetSessionInstanceIdentifier(out IntPtr id);

    [PreserveSig]
    int GetProcessId(out uint processId);

    /// <summary>S_OK (0) for the system sounds session, S_FALSE (1) otherwise.</summary>
    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, ref Guid context);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
