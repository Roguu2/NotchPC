using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using QRCoder;

namespace NotchPC.App.Remote;

/// <summary>The phone tab of the notch: turn the remote on, the pairing QR code, how many phones are connected.</summary>
internal sealed class PhoneViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly RemoteServer _server;
    private readonly DispatcherTimer _renew;
    private ImageSource? _qr;
    private string? _qrUrl;
    private bool _visible;

    public PhoneViewModel(SettingsService settings, RemoteServer server)
    {
        _settings = settings;
        _server = server;
        _renew = new DispatcherTimer();
        _renew.Tick += (_, _) => Refresh();
        _server.StateChanged += (_, _) => Refresh();
        _settings.Changed += (_, _) => RaiseTexts();
        Loc.CultureChanged += (_, _) => RaiseTexts();
    }

    public bool Enabled
    {
        get => _settings.Current.Remote.Enabled;
        set => _settings.Update(s => s.Remote.Enabled = value);
    }

    public bool IsRunning => _server.IsRunning && _server.Address is not null;

    public ImageSource? Qr => _qr;

    public string StatusText => _server.Error is { } error
        ? Loc.Format("Remote.Status.Error", error)
        : !Enabled
            ? Loc.Get("Remote.Status.Off")
            : _server.IsRunning && _server.Address is { } address
                ? Loc.Format("Remote.Status.On", address)
                : _server.IsRunning ? Loc.Get("Remote.Status.NoNetwork") : Loc.Get("Remote.Status.Starting");

    public string DevicesText => Loc.Format("Remote.Devices", _settings.Current.Remote.Devices.Count, _server.ConnectedCount);

    /// <summary>The QR code is shown (and its one-time code kept fresh) only while the tab is open.</summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible)
        {
            Refresh();
        }
        else
        {
            _renew.Stop();
        }
    }

    public void NewCode()
    {
        _server.RenewPairingCode();
        Refresh();
    }

    private void Refresh()
    {
        _renew.Stop();
        var url = _visible ? _server.PairingUrl() : null;
        if (url != _qrUrl)
        {
            _qrUrl = url;
            _qr = url is null ? null : Render(url);
            Raise(nameof(Qr));
        }

        if (_visible && url is not null)
        {
            // Codes live 10 minutes: look again a little after this one expires.
            _renew.Interval = TimeSpan.FromSeconds(30);
            _renew.Start();
        }

        RaiseTexts();
    }

    private void RaiseTexts()
    {
        Raise(nameof(Enabled));
        Raise(nameof(IsRunning));
        Raise(nameof(StatusText));
        Raise(nameof(DevicesText));
    }

    private static BitmapImage Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
