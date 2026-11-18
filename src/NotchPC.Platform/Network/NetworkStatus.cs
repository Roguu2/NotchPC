using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;

namespace NotchPC.Platform.Network;

public enum NetworkKind
{
    None,
    Ethernet,
    WiFi,
    Other,
}

/// <summary>What the network tile shows.</summary>
/// <param name="Name">Wi-Fi network name (SSID) or the Windows network profile name; may be null.</param>
public sealed record NetworkState(NetworkKind Kind, string? Name, bool Internet, bool Vpn, bool HasWifiAdapter);

/// <summary>
/// Network status without polling: read on demand, <see cref="Changed"/> comes from Windows
/// (NetworkStatusChanged). On Windows 11 24H2+ the Wi-Fi name is only given to desktop apps when
/// "Let desktop apps access your location" is on; otherwise <see cref="NetworkState.Name"/> is the profile name.
/// </summary>
public sealed class NetworkStatus : IDisposable
{
    private bool _listening;

    public event EventHandler? Changed;

    public static NetworkState Read()
    {
        var interfaces = SafeInterfaces();
        var hasWifi = interfaces.Any(i => i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        var vpn = interfaces.Any(i => i.OperationalStatus == OperationalStatus.Up && IsVpn(i));

        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                return new NetworkState(NetworkKind.None, null, false, vpn, hasWifi);
            }

            var internet = profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            NetworkKind kind;
            string? name = profile.ProfileName;
            if (profile.IsWlanConnectionProfile)
            {
                kind = NetworkKind.WiFi;
                var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                if (!string.IsNullOrEmpty(ssid))
                {
                    name = ssid;
                }
            }
            else
            {
                kind = profile.NetworkAdapter?.IanaInterfaceType switch
                {
                    6 => NetworkKind.Ethernet,   // ethernetCsmacd
                    71 => NetworkKind.WiFi,      // ieee80211
                    _ => NetworkKind.Other,
                };
            }

            return new NetworkState(kind, name, internet, vpn, hasWifi);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            var up = interfaces.FirstOrDefault(i => i.OperationalStatus == OperationalStatus.Up && !IsVpn(i));
            var kind = up?.NetworkInterfaceType switch
            {
                null => NetworkKind.None,
                NetworkInterfaceType.Wireless80211 => NetworkKind.WiFi,
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => NetworkKind.Ethernet,
                _ => NetworkKind.Other,
            };
            return new NetworkState(kind, null, up is not null, vpn, hasWifi);
        }
    }

    /// <summary>Listens for changes only while someone looks at the tile.</summary>
    public void SetListening(bool listen)
    {
        if (listen == _listening)
        {
            return;
        }

        _listening = listen;
        if (listen)
        {
            NetworkInformation.NetworkStatusChanged += OnStatusChanged;
        }
        else
        {
            NetworkInformation.NetworkStatusChanged -= OnStatusChanged;
        }
    }

    public void Dispose() => SetListening(false);

    private static bool IsVpn(NetworkInterface i) =>
        i.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel ||
        i.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
        i.Description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
        i.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
        i.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase);

    private static NetworkInterface[] SafeInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private void OnStatusChanged(object? sender) => Changed?.Invoke(this, EventArgs.Empty);
}
