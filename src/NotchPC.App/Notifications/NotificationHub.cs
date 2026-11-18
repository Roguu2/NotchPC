using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Notifications;
using NotchPC.Platform.Audio;
using NotchPC.Platform.Notifications;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Notifications;

/// <summary>
/// One place for every notification: NotchPC's own ones (timer, calendar…) and Windows notifications of other apps.
/// Keeps the history for the notifications tab and asks the notch to show a banner. Windows notifications arrive
/// live only with package identity; without it they are read from the Action Center when the tab opens.
/// </summary>
internal sealed class NotificationHub : IDisposable
{
    private const int LogoSize = 48;

    private readonly SettingsService _settings;
    private readonly SystemNotifications _system = new();
    private readonly NotificationHistory _history = new();
    private readonly Dictionary<string, ImageSource?> _logos = new(StringComparer.OrdinalIgnoreCase);
    private int _counter;
    private (string Sound, int Volume, byte[]? Wav) _sound;
    private DateTime _lastSound;

    public NotificationHub(SettingsService settings)
    {
        _settings = settings;
        Current = this;
        _system.Trace = AppLog.Info;
        _system.Added += (_, id) => Dispatch(() => _ = OnSystemAddedAsync(id));
        _system.Removed += (_, id) => Dispatch(() =>
        {
            if (_history.Remove(SystemKey(id)))
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        });
        StartLive();
    }

    /// <summary>For modules that only need to post (timer, calendar).</summary>
    public static NotificationHub? Current { get; private set; }

    public event EventHandler<NotchNotification>? BannerRequested;

    public event EventHandler? HistoryChanged;

    /// <summary>A click asks for a tab of the notch (id).</summary>
    public event EventHandler<string>? TabRequested;

    /// <summary>A click leads to another app: the notch should get out of the way first.</summary>
    public event EventHandler? CollapseRequested;

    public IReadOnlyList<NotchNotification> Items => _history.Items;

    /// <summary>New Windows notifications arrive as they come (identity registered and access allowed).</summary>
    public bool IsLive => _system.IsWatching;

    public NotificationAccess Access => _system.Access;

    public Task<NotificationAccess> RequestAccessAsync() => _system.RequestAccessAsync();

    /// <summary>Starts the live feed if possible (identity + access). Safe to call again.</summary>
    public bool StartLive() => PackageIdentity.HasIdentity && _system.StartWatching();

    public ImageSource? LogoFor(NotchNotification n) =>
        n.AppId is { } id && _logos.TryGetValue(id, out var logo) ? logo : null;

    /// <summary>A notification from NotchPC itself.</summary>
    /// <param name="target">Where a click leads: a web link or <see cref="NotificationTargets.Tab"/>.</param>
    public void Post(string title, string body, string glyph, string? appName = null, string? target = null)
    {
        var n = new NotchNotification(
            $"app:{++_counter}:{DateTime.UtcNow.Ticks}",
            NotificationSource.App,
            appName ?? "NotchPC",
            title,
            body,
            DateTimeOffset.Now,
            glyph,
            Target: target);
        _history.Add(n);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        RequestBanner(n);
    }

    /// <summary>Re-reads the Windows Action Center (when the tab opens).</summary>
    public async Task RefreshSystemAsync()
    {
        var toasts = await _system.ReadAllAsync().ConfigureAwait(true);
        foreach (var t in toasts.GroupBy(t => t.AppId).Select(g => g.First()))
        {
            await EnsureLogoAsync(t).ConfigureAwait(true);
        }

        _history.ReplaceSystem(toasts.Select(ToNotification));
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes it from the list; a Windows notification is removed from the Action Center too.</summary>
    public void Dismiss(NotchNotification n)
    {
        ArgumentNullException.ThrowIfNull(n);
        _history.Remove(n.Key);
        if (n.SystemId is { } id)
        {
            _system.Remove(id);
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A click on a notification. Windows notification: its app comes forward (or starts); Windows does not say
    /// where inside the app it leads. NotchPC notification: its tab or web link. It stays in the list.
    /// </summary>
    public void Open(NotchNotification n)
    {
        ArgumentNullException.ThrowIfNull(n);
        if (NotificationTargets.TabOf(n.Target) is { } tab)
        {
            TabRequested?.Invoke(this, tab);
            return;
        }

        var link = NotificationTargets.WebLinkOf(n.Target);
        if (n.Source == NotificationSource.System ? string.IsNullOrEmpty(n.AppId) : link is null)
        {
            return; // nothing to open (e.g. the sample)
        }

        CollapseRequested?.Invoke(this, EventArgs.Empty);
        var ok = n.Source == NotificationSource.System ? AppActivator.Activate(n.AppId!) : AppActivator.OpenLink(link!);
        if (!ok)
        {
            AppLog.Info($"Notification click: could not open ({n.Source})");
        }
    }

    /// <summary>The Windows notification center, where a click on the original goes exactly where it leads.</summary>
    public void OpenInWindows()
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
        _ = AppActivator.OpenNotificationCenter();
    }

    /// <summary>Plays the notification sound from the settings (also the preview button).</summary>
    public void PlaySound()
    {
        var s = _settings.Current.Notifications;
        if (_sound.Wav is null || _sound.Sound != s.Sound || _sound.Volume != s.SoundVolume)
        {
            _sound = (s.Sound, s.SoundVolume, ChimeSynth.Build(s.Sound, s.SoundVolume));
        }

        if (_sound.Wav is { } wav)
        {
            _lastSound = DateTime.UtcNow;
            SoundPlayback.Play(wav);
        }
    }

    public void ClearAll()
    {
        _system.ClearAll();
        _history.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _system.Dispose();
        if (Current == this)
        {
            Current = null;
        }
    }

    private static string SystemKey(uint id) => $"sys:{id}";

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    private static NotchNotification ToNotification(SystemToast t)
    {
        var (title, body) = ToastText.Split(t.Texts);
        return new NotchNotification(SystemKey(t.Id), NotificationSource.System, t.AppName, title, body, t.Time,
            Glyph: "", SystemId: t.Id, AppId: t.AppId);
    }

    private static BitmapImage? Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = LogoSize;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task OnSystemAddedAsync(uint id)
    {
        if (_system.Read(id) is not { } toast)
        {
            return;
        }

        await EnsureLogoAsync(toast).ConfigureAwait(true);
        var n = ToNotification(toast);
        if (_history.Add(n))
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            RequestBanner(n);
        }
    }

    private async Task EnsureLogoAsync(SystemToast toast)
    {
        if (_logos.ContainsKey(toast.AppId))
        {
            return;
        }

        _logos[toast.AppId] = null; // do not ask twice
        _logos[toast.AppId] = Decode(await _system.ReadLogoAsync(toast.Id, LogoSize).ConfigureAwait(true));
    }

    private void RequestBanner(NotchNotification n)
    {
        var s = _settings.Current.Notifications;
        if (BannerPolicy.ShouldShow(n, s.ShowAppBanners, s.ShowSystemBanners, s.RespectBusy, UserPresenceReader.Read()))
        {
            BannerRequested?.Invoke(this, n);
            if (DateTime.UtcNow - _lastSound > TimeSpan.FromSeconds(1.5)) // a burst of notifications rings once
            {
                PlaySound();
            }
        }
        else
        {
            AppLog.Info($"Notification banner skipped ({n.Source})");
        }
    }
}
