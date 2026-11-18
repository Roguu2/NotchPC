using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NotchPC.Platform.Shell;

/// <summary>Small system switches for the quick settings tab. Each one says what it really changes.</summary>
public static partial class SystemToggles
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const int WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    /// <summary>Windows and apps in dark mode (Settings → Personalization → Colors → "Choose your mode").</summary>
    public static bool IsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int apps && apps == 0;
    }

    /// <summary>
    /// Sets the same two registry values as the Settings app (apps + Windows). Call
    /// <see cref="NotifyThemeChanged"/> afterwards, off the UI thread.
    /// </summary>
    public static void SetDarkMode(bool dark)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
        key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Tells running programs that the theme changed (WM_SETTINGCHANGE "ImmersiveColorSet"), as the Settings app
    /// does. Waits for every top-level window in turn (up to 1 s each for a slow one), so never call it on a
    /// UI thread. Apps that do not follow the system theme stay as they are.
    /// </summary>
    public static void NotifyThemeChanged() =>
        SendMessageTimeoutW(new IntPtr(0xFFFF), WmSettingChange, IntPtr.Zero, "ImmersiveColorSet", SmtoAbortIfHung, 1000, out _);

    /// <summary>
    /// Keeps the PC and the screen awake while on (SetThreadExecutionState). Must be called from a thread that
    /// lives as long as the app (the UI thread). Ends when the app exits.
    /// </summary>
    public static bool SetKeepAwake(bool on) =>
        SetThreadExecutionState(on ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous) != 0;

    /// <summary>Locks the PC (same as Win+L).</summary>
    public static bool Lock() => LockWorkStation();

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeoutW(IntPtr hwnd, int msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
