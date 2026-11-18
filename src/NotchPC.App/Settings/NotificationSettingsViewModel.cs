using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchPC.App.Infrastructure;
using NotchPC.App.Notifications;
using NotchPC.Core.Localization;
using NotchPC.Core.Notifications;
using NotchPC.Platform.Notifications;

namespace NotchPC.App.Settings;

/// <summary>The "Notifications" section of the settings window.</summary>
internal sealed class NotificationSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly NotificationHub _hub;
    private bool _busy;
    private string? _error;

    public NotificationSettingsViewModel(SettingsService settings, NotificationHub hub)
    {
        _settings = settings;
        _hub = hub;
        BuildOptions();
        Loc.CultureChanged += (_, _) =>
        {
            BuildOptions();
            Raise(nameof(SoundOptions));
            Raise(nameof(GlowColorOptions));
            Refresh();
        };
    }

    public static IReadOnlyList<int> SecondsOptions { get; } = [3, 5, 8, 12];

    public static IReadOnlyList<int> ReminderOptions { get; } = [0, 5, 10, 15, 30];

    /// <summary>Built once per language: a new list on every change would make the combo boxes re-select.</summary>
    public IReadOnlyList<SelectOption> SoundOptions { get; private set; } = [];

    public IReadOnlyList<ColorOption> GlowColorOptions { get; private set; } = [];

    public string Sound
    {
        get => _settings.Current.Notifications.Sound;
        set
        {
            Update(s => s.Sound = NotificationSounds.Normalize(value));
            _hub.PlaySound(); // let the user hear the choice right away
        }
    }

    public bool HasSound => Sound != NotificationSounds.None;

    public int SoundVolume
    {
        get => _settings.Current.Notifications.SoundVolume;
        set => Update(s => s.SoundVolume = Math.Clamp(value, 0, 100));
    }

    public string SoundVolumeText => $"{SoundVolume}%";

    public bool Glow
    {
        get => _settings.Current.Notifications.Glow;
        set => Update(s => s.Glow = value);
    }

    public string GlowColor
    {
        get => _settings.Current.Notifications.GlowColor;
        set => Update(s => s.GlowColor = GlowColors.Normalize(value));
    }

    public bool ShowAppBanners
    {
        get => _settings.Current.Notifications.ShowAppBanners;
        set => Update(s => s.ShowAppBanners = value);
    }

    public bool ShowSystemBanners
    {
        get => _settings.Current.Notifications.ShowSystemBanners;
        set => Update(s => s.ShowSystemBanners = value);
    }

    public bool RespectBusy
    {
        get => _settings.Current.Notifications.RespectBusy;
        set => Update(s => s.RespectBusy = value);
    }

    public int BannerSeconds
    {
        get => _settings.Current.Notifications.BannerSeconds;
        set => Update(s => s.BannerSeconds = value);
    }

    public int ReminderMinutes
    {
        get => _settings.Current.Notifications.CalendarReminderMinutes;
        set => Update(s => s.CalendarReminderMinutes = value);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Bound from XAML.")]
    public bool IsRegistered => PackageIdentity.IsRegistered();

    /// <summary>Registered, but this process started before that: a restart gives it the identity.</summary>
    public bool NeedsRestart => IsRegistered && !PackageIdentity.HasIdentity;

    public bool CanRegister => !_busy && !IsRegistered && PackageIdentity.IsDeveloperModeOn();

    public bool CanUnregister => !_busy && IsRegistered;

    /// <summary>Plain-language state of the live Windows notifications.</summary>
    public string IdentityStatus
    {
        get
        {
            if (_error is not null)
            {
                return Loc.Format("Settings.Notify.Identity.Error", _error);
            }

            if (_hub.Access == NotificationAccess.Denied)
            {
                return Loc.Get("Settings.Notify.Identity.Denied");
            }

            if (_hub.IsLive)
            {
                return Loc.Get("Settings.Notify.Identity.Live");
            }

            if (NeedsRestart)
            {
                return Loc.Get("Settings.Notify.Identity.Restart");
            }

            return PackageIdentity.IsDeveloperModeOn()
                ? Loc.Get("Settings.Notify.Identity.Off")
                : Loc.Get("Settings.Notify.Identity.NoDevMode");
        }
    }

    /// <summary>
    /// Where the package manifest lives (it must stay there while registered). Not %AppData% or %LocalAppData%\NotchPC:
    /// when NotchPC itself runs inside another app's MSIX container (e.g. started from a packaged terminal), Windows
    /// redirects those writes into that app's private cache and the deployment service then cannot find the
    /// manifest (0x80070003). %LocalAppData%\Programs is not redirected.
    /// </summary>
    public static string PackageDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NotchPC", "identity");

    public async Task RegisterAsync()
    {
        _busy = true;
        _error = null;
        Refresh();
        try
        {
            WriteLogos(Path.Combine(PackageDirectory, "Assets"));
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
            _error = await PackageIdentity.RegisterAsync(PackageDirectory, Path.GetDirectoryName(exe)!, Path.GetFileName(exe)).ConfigureAwait(true);
            AppLog.Info(_error is null ? "Package identity registered" : $"Package identity registration failed: {_error}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            _error = ex.Message;
            AppLog.Error("Package identity registration failed", ex);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    public async Task UnregisterAsync()
    {
        _busy = true;
        _error = null;
        Refresh();
        _error = await PackageIdentity.UnregisterAsync().ConfigureAwait(true);
        AppLog.Info(_error is null ? "Package identity removed" : $"Package identity removal failed: {_error}");
        _busy = false;
        Refresh();
    }

    public void PlaySound() => _hub.PlaySound();

        /// <summary>Shows what a NotchPC notification looks like.</summary>
    public void ShowSample() =>
        _hub.Post(Loc.Get("Notify.Sample.Title"), Loc.Get("Notify.Sample.Body"), "");

    public void Refresh()
    {
        foreach (var name in new[] { nameof(IsRegistered), nameof(NeedsRestart), nameof(CanRegister), nameof(CanUnregister), nameof(IdentityStatus) })
        {
            Raise(name);
        }
    }

    /// <summary>The package needs three logos; they are drawn from the app icon.</summary>
    private static void WriteLogos(string folder)
    {
        Directory.CreateDirectory(folder);
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/NotchPC.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        foreach (var (name, size) in new[] { ("Square150.png", 150), ("Square44.png", 44), ("StoreLogo.png", 50) })
        {
            var scaled = new TransformedBitmap(source, new ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using var file = File.Create(Path.Combine(folder, name));
            encoder.Save(file);
        }
    }

    private void BuildOptions()
    {
        SoundOptions = NotificationSounds.All.Select(id => new SelectOption(id, Loc.Get("Settings.Notify.Sound." + id))).ToList();
        GlowColorOptions = GlowColors.All
            .Select(id => new ColorOption(id, Loc.Get("Settings.Notify.Glow." + (id == GlowColors.Accent ? "Accent" : id.TrimStart('#'))), Swatch(id)))
            .ToList();
    }

    private static SolidColorBrush Swatch(string id)
    {
        var brush = new SolidColorBrush(GlowColorResolver.Resolve(id));
        brush.Freeze();
        return brush;
    }

    private void Update(Action<Core.Settings.NotificationSettings> change)
    {
        _settings.Update(s => change(s.Notifications));
        foreach (var name in new[]
                 {
                     nameof(ShowAppBanners), nameof(ShowSystemBanners), nameof(RespectBusy), nameof(BannerSeconds), nameof(ReminderMinutes),
                     nameof(Sound), nameof(HasSound), nameof(SoundVolume), nameof(SoundVolumeText), nameof(Glow), nameof(GlowColor),
                 })
        {
            Raise(name);
        }
    }
}

/// <summary>A colour choice with its sample.</summary>
public sealed record ColorOption(string Value, string Display, Brush Swatch)
{
    public override string ToString() => Display;
}
