using System.Runtime.InteropServices;

namespace NotchPC.Platform.Audio;

/// <summary>An active playback or recording device.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>Lists audio devices and changes the Windows default playback / recording device.</summary>
public static partial class AudioDevices
{
    private const uint DeviceStateActive = 0x1;
    private const ushort VtLpwstr = 31;

    // PKEY_Device_FriendlyName ("Speakers (Realtek(R) Audio)")
    private static readonly Guid FriendlyNameFormat = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    /// <summary>Active devices of the given kind, the default one marked. Empty when Core Audio is unavailable.</summary>
    public static IReadOnlyList<AudioDevice> List(AudioFlow flow)
    {
        var dataFlow = flow == AudioFlow.Output ? DataFlow.Render : DataFlow.Capture;
        var role = flow == AudioFlow.Output ? DeviceRole.Multimedia : DeviceRole.Console;
        var result = new List<AudioDevice>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(dataFlow, role, out var def) == 0 && def is not null)
            {
                def.GetId(out defaultId);
                Marshal.ReleaseComObject(def);
            }

            if (enumerator.EnumAudioEndpoints(dataFlow, DeviceStateActive, out var collectionPtr) != 0 || collectionPtr == IntPtr.Zero)
            {
                return result;
            }

            var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collectionPtr);
            Marshal.Release(collectionPtr);
            try
            {
                collection.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0 || device is null)
                    {
                        continue;
                    }

                    try
                    {
                        device.GetId(out var id);
                        result.Add(new AudioDevice(id, FriendlyName(device) ?? id, id == defaultId));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        catch (COMException)
        {
            // Audio service stopped: show nothing.
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Makes the device the default for all roles (as the Windows sound flyout does). Uses the undocumented
    /// IPolicyConfig interface: Windows has no public API for this. Returns false if Windows refused.
    /// </summary>
    public static bool SetDefault(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigComObject();
            try
            {
                var ok = true;
                foreach (var role in new[] { DeviceRole.Console, DeviceRole.Multimedia, DeviceRole.Communications })
                {
                    ok &= policy.SetDefaultEndpoint(deviceId, role) == 0;
                }

                return ok;
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var storePtr) != 0 || storePtr == IntPtr.Zero)
        {
            return null;
        }

        var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
        Marshal.Release(storePtr);
        try
        {
            var key = new PropertyKey { FormatId = FriendlyNameFormat, PropertyId = 14 };
            if (store.GetValue(ref key, out var value) != 0)
            {
                return null;
            }

            try
            {
                return value.VarType == VtLpwstr ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);
}
