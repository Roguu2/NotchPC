using System.Collections.ObjectModel;
using System.Windows.Media;
using NotchPC.App.Clipboard;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Core.Notifications;
using NotchPC.Platform.Notifications;

namespace NotchPC.App.Notifications;

/// <summary>Notifications tab: own and Windows notifications, newest first.</summary>
internal sealed class NotificationsViewModel : ObservableObject
{
    private readonly NotificationHub _hub;
    private bool _visible;

    public NotificationsViewModel(NotificationHub hub)
    {
        _hub = hub;
        _hub.HistoryChanged += (_, _) =>
        {
            if (_visible)
            {
                Rebuild();
            }
        };
        Loc.CultureChanged += (_, _) => Rebuild();
    }

    public ObservableCollection<NotificationItem> Items { get; } = [];

    public NotificationHub Hub => _hub;

    public bool IsEmpty => Items.Count == 0;

    public string CountText => Loc.Format("Notify.Count", Items.Count);

    public bool AccessDenied => _hub.Access == NotificationAccess.Denied;

    /// <summary>One line under the list: live, or only refreshed when the tab opens (and why).</summary>
    public string StatusText => AccessDenied ? Loc.Get("Notify.Status.Denied")
        : _hub.IsLive ? Loc.Get("Notify.Status.Live")
        : Loc.Get("Notify.Status.OnOpen");

    public async void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible)
        {
            return;
        }

        Rebuild();
        await _hub.RefreshSystemAsync().ConfigureAwait(true);
    }

    public void Dismiss(NotificationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _hub.Dismiss(item.Notification);
    }

    public void Open(NotificationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _hub.Open(item.Notification);
    }

    public void OpenInWindows() => _hub.OpenInWindows();

    public void ClearAll() => _hub.ClearAll();

    private void Rebuild()
    {
        var now = DateTimeOffset.Now;
        Items.Clear();
        foreach (var n in _hub.Items)
        {
            Items.Add(new NotificationItem(n, _hub.LogoFor(n), now));
        }

        Raise(nameof(IsEmpty));
        Raise(nameof(CountText));
        Raise(nameof(StatusText));
        Raise(nameof(AccessDenied));
    }
}

/// <summary>One row of the notifications list (also the content of the banner).</summary>
internal sealed class NotificationItem(NotchNotification notification, ImageSource? logo, DateTimeOffset now)
{
    public NotchNotification Notification { get; } = notification;

    public ImageSource? Logo { get; } = logo;

    public bool HasLogo => Logo is not null;

    public string Glyph => Notification.Glyph ?? "";

    public string Header => $"{Notification.AppName} · {ClipItemViewModel.TimeText(Notification.Time, now)}";

    public string Title => string.IsNullOrEmpty(Notification.Title) ? Notification.AppName : Notification.Title;

    public string Body => Notification.Body;

    public bool HasBody => !string.IsNullOrEmpty(Notification.Body);

    /// <summary>A Windows notification: it can also be opened in the Windows notification center.</summary>
    public bool IsSystem => Notification.Source == NotificationSource.System;

    /// <summary>A click leads somewhere (hand cursor).</summary>
    public bool CanOpen => IsSystem ? !string.IsNullOrEmpty(Notification.AppId) : Notification.Target is not null;
}
