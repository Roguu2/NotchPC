using Windows.Devices.Radios;

namespace NotchPC.Platform.Network;

public enum RadioType
{
    WiFi,
    Bluetooth,
}

/// <summary>
/// Wi-Fi and Bluetooth on/off (Windows.Devices.Radios, the same switches as in Windows quick settings).
/// A radio that does not exist on this PC is reported as null, and its tile is not shown.
/// </summary>
public static class RadioSwitches
{
    /// <summary>True = on, false = off, null = no such radio (or Windows denied access).</summary>
    public static async Task<bool?> GetAsync(RadioType type)
    {
        var radio = await FindAsync(type).ConfigureAwait(false);
        return radio is null ? null : radio.State == RadioState.On;
    }

    /// <summary>Returns the new state, or null when the radio is missing or Windows refused.</summary>
    public static async Task<bool?> SetAsync(RadioType type, bool on)
    {
        var radio = await FindAsync(type).ConfigureAwait(false);
        if (radio is null)
        {
            return null;
        }

        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        return result == RadioAccessStatus.Allowed ? on : null;
    }

    private static async Task<Radio?> FindAsync(RadioType type)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
            {
                return null;
            }

            var kind = type == RadioType.WiFi ? RadioKind.WiFi : RadioKind.Bluetooth;
            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
