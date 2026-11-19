using System.Collections.ObjectModel;
using NotchPC.App.Infrastructure;
using NotchPC.App.Remote;
using NotchPC.Core.Localization;
using NotchPC.Core.Settings;

namespace NotchPC.App.Settings;

/// <summary>The "Phone remote" section of the settings window: on/off, shutdown delay, paired phones.</summary>
internal sealed class RemoteSettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly RemoteServer _server;

    public RemoteSettingsViewModel(SettingsService settings, RemoteServer server)
    {
        _settings = settings;
        _server = server;
        Rebuild();
        _settings.Changed += (_, _) => Rebuild();
        _server.StateChanged += (_, _) => Rebuild();
        Loc.CultureChanged += (_, _) => Rebuild();
    }

    public static IReadOnlyList<int> DelayOptions { get; } = [0, 10, 30, 60, 120];

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public bool HasDevices => Devices.Count > 0;

    public bool Enabled
    {
        get => _settings.Current.Remote.Enabled;
        set => _settings.Update(s => s.Remote.Enabled = value);
    }

    public int PowerDelay
    {
        get => _settings.Current.Remote.PowerDelaySeconds;
        set => _settings.Update(s => s.Remote.PowerDelaySeconds = value);
    }

    public bool FilesAndClipboard
    {
        get => _settings.Current.Remote.FilesAndClipboard;
        set => _settings.Update(s => s.Remote.FilesAndClipboard = value);
    }

    public string StatusText => _server.Error is { } error
        ? Loc.Format("Remote.Status.Error", error)
        : _server.Address is { } address ? Loc.Format("Remote.Status.On", address) : Loc.Get("Remote.Status.Off");

    public void Remove(DeviceRow row) =>
        _settings.Update(s => s.Remote.Devices.RemoveAll(d => d.Id == row.Id));

    private void Rebuild()
    {
        var devices = _settings.Current.Remote.Devices;
        if (!Devices.Select(d => (d.Id, d.Detail)).SequenceEqual(devices.Select(d => (d.Id, DeviceRow.DetailOf(d)))))
        {
            Devices.Clear();
            foreach (var d in devices.OrderByDescending(d => d.LastSeen ?? d.PairedAt))
            {
                Devices.Add(new DeviceRow(d.Id, d.Name, DeviceRow.DetailOf(d)));
            }
        }

        Raise(nameof(HasDevices));
        Raise(nameof(Enabled));
        Raise(nameof(PowerDelay));
        Raise(nameof(FilesAndClipboard));
        Raise(nameof(StatusText));
    }
}

internal sealed record DeviceRow(string Id, string Name, string Detail)
{
    public static string DetailOf(PairedDevice d) => d.LastSeen is { } seen
        ? Loc.Format("Remote.Device.Seen", d.PairedAt.ToLocalTime().ToString("d", Loc.Culture), seen.ToLocalTime().ToString("g", Loc.Culture))
        : Loc.Format("Remote.Device.Paired", d.PairedAt.ToLocalTime().ToString("d", Loc.Culture));
}
