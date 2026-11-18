using System.Runtime.InteropServices;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace NotchPC.Platform.Notifications;

/// <summary>A Windows notification of another app (as it sits in the Action Center).</summary>
public sealed record SystemToast(uint Id, string AppName, string AppId, IReadOnlyList<string> Texts, DateTimeOffset Time);

public enum NotificationAccess
{
    Allowed,
    Denied,
    Unspecified,
}

/// <summary>
/// Reads Windows notifications of other apps (UserNotificationListener). Reading the Action Center works for a
/// normal desktop app; the live "new notification" event needs package identity (see <see cref="PackageIdentity"/>),
/// without it Windows throws 0x80070490. Only the text and the app logo are available: an app's notification
/// cannot be clicked or answered from here (API limitation).
/// </summary>
public sealed class SystemNotifications : IDisposable
{
    private readonly UserNotificationListener? _listener;
    private bool _watching;

    public SystemNotifications()
    {
        try
        {
            _listener = UserNotificationListener.Current;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            _listener = null;
        }
    }

    /// <summary>A notification was added (id) on a Windows thread pool thread.</summary>
    public event EventHandler<uint>? Added;

    /// <summary>A notification was removed from the Action Center (id).</summary>
    public event EventHandler<uint>? Removed;

    public bool IsWatching => _watching;

    public NotificationAccess Access
    {
        get
        {
            try
            {
                return _listener?.GetAccessStatus() switch
                {
                    UserNotificationListenerAccessStatus.Allowed => NotificationAccess.Allowed,
                    UserNotificationListenerAccessStatus.Denied => NotificationAccess.Denied,
                    _ => NotificationAccess.Unspecified,
                };
            }
            catch (COMException)
            {
                return NotificationAccess.Unspecified;
            }
        }
    }

    /// <summary>Asks Windows for access (may show the system consent prompt). Call on the UI thread.</summary>
    public async Task<NotificationAccess> RequestAccessAsync()
    {
        if (_listener is null)
        {
            return NotificationAccess.Unspecified;
        }

        try
        {
            await _listener.RequestAccessAsync();
        }
        catch (COMException)
        {
        }

        return Access;
    }

    /// <summary>Subscribes to new / removed notifications. False when Windows does not allow it (no identity).</summary>
    public bool StartWatching()
    {
        if (_watching || _listener is null || Access != NotificationAccess.Allowed)
        {
            return _watching;
        }

        try
        {
            _listener.NotificationChanged += OnChanged;
            _watching = true;
        }
        catch (COMException)
        {
            _watching = false;
        }

        return _watching;
    }

    public async Task<IReadOnlyList<SystemToast>> ReadAllAsync()
    {
        if (_listener is null || Access != NotificationAccess.Allowed)
        {
            return [];
        }

        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            return list.Select(Convert).Where(t => t is not null).Select(t => t!).ToList();
        }
        catch (Exception ex)
        {
            Trace?.Invoke("ReadAll failed: " + ex.GetType().Name + " " + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            return [];
        }
    }

    /// <summary>Diagnostics sink (the app log).</summary>
    public Action<string>? Trace { get; set; }

    public SystemToast? Read(uint id)
    {
        try
        {
            return _listener?.GetNotification(id) is { } n ? Convert(n) : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>App logo as PNG bytes (small), or null.</summary>
    public async Task<byte[]?> ReadLogoAsync(uint id, int size)
    {
        try
        {
            if (_listener?.GetNotification(id)?.AppInfo?.DisplayInfo is not { } info)
            {
                return null;
            }

            var reference = info.GetLogo(new Windows.Foundation.Size(size, size));
            if (reference is null)
            {
                return null;
            }

            using var stream = await reference.OpenReadAsync();
            if (stream is null || stream.Size == 0)
            {
                return null;
            }

            var bytes = new byte[stream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or NullReferenceException)
        {
            // Some apps have no logo for this size: the projection then fails inside WinRT (seen as a null reference).
            return null;
        }
    }

    /// <summary>Removes it from the Windows Action Center too.</summary>
    public void Remove(uint id)
    {
        try
        {
            _listener?.RemoveNotification(id);
        }
        catch (COMException)
        {
        }
    }

    public void ClearAll()
    {
        try
        {
            _listener?.ClearNotifications();
        }
        catch (COMException)
        {
        }
    }

    public void Dispose()
    {
        if (_watching && _listener is not null)
        {
            try
            {
                _listener.NotificationChanged -= OnChanged;
            }
            catch (COMException)
            {
            }
        }

        _watching = false;
    }

    private static SystemToast? Convert(UserNotification n)
    {
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).ToList() ?? [];
            var app = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            var appId = n.AppInfo?.AppUserModelId ?? app;
            return new SystemToast(n.Id, app, appId, texts, n.CreationTime);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void OnChanged(UserNotificationListener sender, Windows.UI.Notifications.UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Added)
        {
            Added?.Invoke(this, args.UserNotificationId);
        }
        else
        {
            Removed?.Invoke(this, args.UserNotificationId);
        }
    }
}
