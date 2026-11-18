using System.Runtime.InteropServices;

namespace NotchPC.Platform.Shell;

/// <summary>A system-wide keyboard shortcut (RegisterHotKey): works in games too, nothing is hooked.</summary>
public static partial class GlobalHotkey
{
    public const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    /// <summary>False when another app already owns this combination.</summary>
    public static bool Register(IntPtr hwnd, int id, uint modifiers, uint key) =>
        RegisterHotKey(hwnd, id, modifiers | ModNoRepeat, key);

    public static void Unregister(IntPtr hwnd, int id) => _ = UnregisterHotKey(hwnd, id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hwnd, int id);
}
