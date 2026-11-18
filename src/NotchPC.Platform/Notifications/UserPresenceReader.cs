using System.Runtime.InteropServices;
using NotchPC.Core.Notifications;

namespace NotchPC.Platform.Notifications;

/// <summary>
/// SHQueryUserNotificationState: the same check Windows uses before showing a toast (fullscreen app,
/// presentation mode, quiet hours / Do not disturb). Cheap, called only when a notification arrives.
/// </summary>
public static partial class UserPresenceReader
{
    public static UserPresence Read()
    {
        if (SHQueryUserNotificationState(out var state) != 0)
        {
            return UserPresence.Unknown;
        }

        return state switch
        {
            5 => UserPresence.AcceptsNotifications,  // QUNS_ACCEPTS_NOTIFICATIONS
            1 => UserPresence.AcceptsNotifications,  // QUNS_NOT_PRESENT (locked / screensaver): history only anyway
            2 or 3 or 4 or 6 or 7 => UserPresence.Busy, // busy, D3D fullscreen, presentation, quiet time, app (Win8+)
            _ => UserPresence.Unknown,
        };
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
