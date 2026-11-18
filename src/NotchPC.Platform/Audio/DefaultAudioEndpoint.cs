using System.Runtime.InteropServices;

namespace NotchPC.Platform.Audio;

public enum AudioFlow
{
    /// <summary>Default playback device (speakers/headphones).</summary>
    Output,

    /// <summary>Default recording device (microphone).</summary>
    Input,
}

/// <summary>
/// Master volume and mute of the Windows default playback or recording device. Follows default-device changes.
/// Event-driven (Core Audio callbacks); <see cref="Changed"/> is raised on the creating thread's context.
/// </summary>
public sealed class DefaultAudioEndpoint : IDisposable
{
    private static readonly Guid AudioEndpointVolumeId = typeof(IAudioEndpointVolume).GUID;
    private const uint ClsCtxAll = 0x17;

    private readonly AudioFlow _flow;
    private readonly SynchronizationContext? _context;
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationSink _sink;
    private readonly Lock _gate = new();
    private Guid _eventContext = Guid.NewGuid();
    private IAudioEndpointVolume? _volume;
    private string? _deviceId;
    private bool _disposed;

    public DefaultAudioEndpoint(AudioFlow flow)
    {
        _flow = flow;
        _context = SynchronizationContext.Current;
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _sink = new NotificationSink(this);
        _enumerator.RegisterEndpointNotificationCallback(_sink);
        Bind();
    }

    public event EventHandler? Changed;

    /// <summary>False when there is no such device (e.g. no microphone plugged in).</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _volume is not null;
            }
        }
    }

    /// <summary>0..1, as in the Windows volume slider.</summary>
    /// <summary>Id of the current default device (changes when the user switches devices).</summary>
    public string? DeviceId
    {
        get
        {
            lock (_gate)
            {
                return _deviceId;
            }
        }
    }

    public float Volume
    {
        get
        {
            lock (_gate)
            {
                return _volume is not null && _volume.GetMasterVolumeLevelScalar(out var v) == 0 ? v : 0f;
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_gate)
            {
                return _volume is not null && _volume.GetMute(out var m) == 0 && m;
            }
        }
    }

    public void SetVolume(float value)
    {
        lock (_gate)
        {
            _volume?.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref _eventContext);
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            _volume?.SetMute(muted, ref _eventContext);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enumerator.UnregisterEndpointNotificationCallback(_sink);
        lock (_gate)
        {
            ReleaseVolume();
        }

        Marshal.ReleaseComObject(_enumerator);
    }

    private void Bind()
    {
        lock (_gate)
        {
            ReleaseVolume();
            var dataFlow = _flow == AudioFlow.Output ? DataFlow.Render : DataFlow.Capture;
            var role = _flow == AudioFlow.Output ? DeviceRole.Multimedia : DeviceRole.Console;
            if (_enumerator.GetDefaultAudioEndpoint(dataFlow, role, out var device) != 0 || device is null)
            {
                return; // no device of this kind
            }

            try
            {
                device.GetId(out _deviceId);
                var iid = AudioEndpointVolumeId;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var obj) == 0 && obj is IAudioEndpointVolume volume)
                {
                    _volume = volume;
                    _volume.RegisterControlChangeNotify(_sink);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
    }

    private void ReleaseVolume()
    {
        if (_volume is null)
        {
            return;
        }

        _volume.UnregisterControlChangeNotify(_sink);
        Marshal.ReleaseComObject(_volume);
        _volume = null;
        _deviceId = null;
    }

    private void Raise()
    {
        if (_disposed)
        {
            return;
        }

        if (_context is null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _context.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
        }
    }

    private void OnDefaultDeviceChanged(DataFlow flow, DeviceRole role)
    {
        var mine = _flow == AudioFlow.Output
            ? flow == DataFlow.Render && role == DeviceRole.Multimedia
            : flow == DataFlow.Capture && role == DeviceRole.Console;
        if (!mine || _disposed)
        {
            return;
        }

        // Core Audio forbids re-entering the API inside its callback thread: rebind on the thread pool.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed)
            {
                return;
            }

            Bind();
            Raise();
        });
    }

    [ComVisible(true)]
    private sealed class NotificationSink(DefaultAudioEndpoint owner) : IAudioEndpointVolumeCallback, IMMNotificationClient
    {
        public int OnNotify(IntPtr notifyData)
        {
            owner.Raise();
            return 0;
        }

        public int OnDefaultDeviceChanged(DataFlow flow, DeviceRole role, string? defaultDeviceId)
        {
            owner.OnDefaultDeviceChanged(flow, role);
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
