using System.Collections.ObjectModel;
using System.Diagnostics;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Platform.Audio;
using NotchPC.Platform.Monitors;
using NotchPC.Platform.Network;
using NotchPC.Platform.Shell;

namespace NotchPC.App.QuickSettings;

/// <summary>
/// Quick settings tab: volume and output device, brightness (DDC/CI), microphone, network, Wi-Fi/Bluetooth when
/// present, dark mode, keep awake, lock, and shortcuts to Windows settings that have no public API.
/// Nothing is read or listened to while the tab is closed, except the Core Audio callbacks (free when idle).
/// </summary>
internal sealed class QuickSettingsViewModel : ObservableObject, IDisposable
{
    private readonly NetworkStatus _network = new();
    private DefaultAudioEndpoint? _output;
    private DefaultAudioEndpoint? _mic;
    private string? _outputDeviceId;
    private bool _visible;
    private bool _showDevices;
    private string _outputName = "";
    private int? _brightness;
    private int? _pendingBrightness;
    private bool _writingBrightness;
    private NetworkState _net = new(NetworkKind.None, null, false, false, false);
    private bool? _wifi;
    private bool? _bluetooth;
    private bool _darkMode;
    private bool _keepAwake;

    public QuickSettingsViewModel()
    {
        _network.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshNetwork);
        Loc.CultureChanged += (_, _) => RaiseAll();
    }

    /// <summary>Raised when the number of tiles changes (the panel height follows).</summary>
    public event EventHandler? LayoutChanged;

    public ObservableCollection<AudioDeviceItem> Devices { get; } = [];

    // ------------------------------------------------------------------ volume

    public bool HasOutput => _output?.IsAvailable == true;

    public double Volume
    {
        get => Math.Round((_output?.Volume ?? 0) * 100);
        set
        {
            _output?.SetVolume((float)(value / 100));
            if (value > 0 && _output?.IsMuted == true)
            {
                _output.SetMuted(false);
            }

            Raise();
            Raise(nameof(VolumeText));
            Raise(nameof(VolumeGlyph));
        }
    }

    public string VolumeText => $"{Volume:0}%";

    public bool OutputMuted => _output?.IsMuted == true;

    public string VolumeGlyph => OutputMuted || Volume == 0 ? ""   // Mute
        : Volume < 34 ? "" : Volume < 67 ? "" : "";    // Volume1..3

    public string OutputName => _outputName;

    public bool ShowDevices
    {
        get => _showDevices;
        set
        {
            if (Set(ref _showDevices, value) && value)
            {
                RefreshDevices();
            }
        }
    }

    public void ToggleOutputMute() => _output?.SetMuted(!OutputMuted);

    public void SelectDevice(AudioDeviceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!AudioDevices.SetDefault(item.Device.Id))
        {
            AppLog.Info("Switching the default audio device was refused");
        }

        ShowDevices = false;
        RefreshDevices();
    }

    // ------------------------------------------------------------------ brightness

    public bool HasBrightness => _brightness is not null;

    public double Brightness
    {
        get => _brightness ?? 0;
        set
        {
            var v = (int)Math.Round(value);
            if (v == _brightness)
            {
                return;
            }

            _brightness = v;
            Raise();
            Raise(nameof(BrightnessText));
            QueueBrightness(v);
        }
    }

    public string BrightnessText => _brightness is { } b ? $"{b}%" : "";

    public string BrightnessHint => Loc.Get(HasBrightness ? "Quick.Brightness.Hint" : "Quick.Brightness.Missing");

    // ------------------------------------------------------------------ tiles

    public bool HasMic => _mic?.IsAvailable == true;

    public bool MicOn
    {
        get => HasMic && !_mic!.IsMuted;
        set
        {
            _mic?.SetMuted(!value);
            Raise();
            Raise(nameof(MicStatus));
        }
    }

    public string MicStatus => Loc.Get(!HasMic ? "Quick.None" : MicOn ? "Quick.On" : "Quick.Muted");

    public string NetworkTitle => _net.Kind switch
    {
        NetworkKind.Ethernet => "Ethernet",
        NetworkKind.WiFi => _net.Name ?? "Wi-Fi",
        NetworkKind.Other => _net.Name ?? Loc.Get("Quick.Network"),
        _ => Loc.Get("Quick.Network"),
    };

    public string NetworkStatusText
    {
        get
        {
            var text = Loc.Get(_net.Kind == NetworkKind.None ? "Quick.Network.None" : _net.Internet ? "Quick.Network.Internet" : "Quick.Network.NoInternet");
            return _net.Vpn ? text + " · VPN" : text;
        }
    }

    public string NetworkGlyph => _net.Kind switch
    {
        NetworkKind.WiFi => "",
        NetworkKind.None => "", // network offline
        _ => "",                // ethernet
    };

    public bool NetworkOk => _net.Kind != NetworkKind.None && _net.Internet;

    public bool HasWifi => _wifi is not null;

    public bool WifiOn
    {
        get => _wifi == true;
        set => _ = SetRadioAsync(RadioType.WiFi, value);
    }

    public bool HasBluetooth => _bluetooth is not null;

    public bool BluetoothOn
    {
        get => _bluetooth == true;
        set => _ = SetRadioAsync(RadioType.Bluetooth, value);
    }

    public bool DarkMode
    {
        get => _darkMode;
        set
        {
            if (value == _darkMode)
            {
                return;
            }

            try
            {
                SystemToggles.SetDarkMode(value);
                _darkMode = value;
                _ = Task.Run(SystemToggles.NotifyThemeChanged);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                AppLog.Error("Changing the theme failed", ex);
            }

            Raise();
        }
    }

    public bool KeepAwake
    {
        get => _keepAwake;
        set
        {
            if (SystemToggles.SetKeepAwake(value))
            {
                _keepAwake = value;
            }

            Raise();
            Raise(nameof(KeepAwakeStatus));
        }
    }

    public string KeepAwakeStatus => Loc.Get(_keepAwake ? "Quick.On" : "Quick.Off");

    /// <summary>Rows of tiles (4 per row).</summary>
    public int TileRows => (int)Math.Ceiling(TileCount / 4.0);

    private int TileCount => 7 + (HasWifi ? 1 : 0) + (HasBluetooth ? 1 : 0);

    public static void Lock() => SystemToggles.Lock();

    public static void OpenSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Opening Windows settings failed", ex);
        }
    }

    // ------------------------------------------------------------------ lifecycle

    public void SetVisible(bool visible)
    {
        if (visible == _visible)
        {
            return;
        }

        _visible = visible;
        _network.SetListening(visible);
        if (!visible)
        {
            ShowDevices = false;
            return;
        }

        EnsureAudio();
        _darkMode = SystemToggles.IsDarkMode();
        RefreshNetwork();
        RefreshDevices();
        _ = RefreshBrightnessAsync();
        _ = RefreshRadiosAsync();
        RaiseAll();
    }

    public void Dispose()
    {
        _network.Dispose();
        _output?.Dispose();
        _mic?.Dispose();
        if (_keepAwake)
        {
            SystemToggles.SetKeepAwake(false);
        }
    }

    private void EnsureAudio()
    {
        if (_output is not null)
        {
            return;
        }

        _output = TryCreate(AudioFlow.Output);
        _mic = TryCreate(AudioFlow.Input);
        if (_output is not null)
        {
            _output.Changed += (_, _) =>
            {
                if (_output.DeviceId != _outputDeviceId)
                {
                    RefreshDevices(); // the default device was switched (here or in Windows)
                }

                Raise(nameof(Volume));
                Raise(nameof(VolumeText));
                Raise(nameof(VolumeGlyph));
                Raise(nameof(OutputMuted));
                Raise(nameof(HasOutput));
            };
        }

        if (_mic is not null)
        {
            _mic.Changed += (_, _) =>
            {
                Raise(nameof(MicOn));
                Raise(nameof(MicStatus));
                Raise(nameof(HasMic));
            };
        }
    }

    private static DefaultAudioEndpoint? TryCreate(AudioFlow flow)
    {
        try
        {
            return new DefaultAudioEndpoint(flow);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppLog.Error("Core Audio unavailable", ex);
            return null;
        }
    }

    private void RefreshDevices()
    {
        _outputDeviceId = _output?.DeviceId;
        var list = AudioDevices.List(AudioFlow.Output);
        Devices.Clear();
        foreach (var d in list)
        {
            Devices.Add(new AudioDeviceItem(d));
        }

        var current = list.FirstOrDefault(d => d.IsDefault);
        _outputName = current?.Name ?? Loc.Get("Quick.Output.None");
        Raise(nameof(OutputName));
    }

    private void RefreshNetwork()
    {
        _net = NetworkStatus.Read();
        Raise(nameof(NetworkTitle));
        Raise(nameof(NetworkStatusText));
        Raise(nameof(NetworkGlyph));
        Raise(nameof(NetworkOk));
    }

    private async Task RefreshBrightnessAsync()
    {
        if (_writingBrightness)
        {
            return;
        }

        var value = await Task.Run(MonitorBrightness.Read).ConfigureAwait(true);
        if (_writingBrightness)
        {
            return; // the user moved the slider meanwhile
        }

        _brightness = value;
        Raise(nameof(Brightness));
        Raise(nameof(BrightnessText));
        Raise(nameof(HasBrightness));
        Raise(nameof(BrightnessHint));
    }

    /// <summary>DDC/CI is slow: only the newest slider value is sent, one write at a time.</summary>
    private void QueueBrightness(int value)
    {
        _pendingBrightness = value;
        if (!_writingBrightness)
        {
            _ = WriteBrightnessAsync();
        }
    }

    private async Task WriteBrightnessAsync()
    {
        _writingBrightness = true;
        try
        {
            while (_pendingBrightness is { } v)
            {
                _pendingBrightness = null;
                await Task.Run(() => MonitorBrightness.Write(v)).ConfigureAwait(true);
            }
        }
        finally
        {
            _writingBrightness = false;
        }
    }

    private async Task RefreshRadiosAsync()
    {
        var rows = TileRows;
        _wifi = await RadioSwitches.GetAsync(RadioType.WiFi).ConfigureAwait(true);
        _bluetooth = await RadioSwitches.GetAsync(RadioType.Bluetooth).ConfigureAwait(true);
        Raise(nameof(HasWifi));
        Raise(nameof(WifiOn));
        Raise(nameof(HasBluetooth));
        Raise(nameof(BluetoothOn));
        Raise(nameof(TileRows));
        if (rows != TileRows)
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SetRadioAsync(RadioType type, bool on)
    {
        var state = await RadioSwitches.SetAsync(type, on).ConfigureAwait(true);
        if (state is not null)
        {
            if (type == RadioType.WiFi)
            {
                _wifi = state;
            }
            else
            {
                _bluetooth = state;
            }
        }

        Raise(type == RadioType.WiFi ? nameof(WifiOn) : nameof(BluetoothOn));
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(HasOutput), nameof(Volume), nameof(VolumeText), nameof(VolumeGlyph), nameof(OutputMuted), nameof(OutputName),
                     nameof(HasBrightness), nameof(Brightness), nameof(BrightnessText), nameof(BrightnessHint),
                     nameof(HasMic), nameof(MicOn), nameof(MicStatus), nameof(DarkMode), nameof(KeepAwake), nameof(KeepAwakeStatus),
                     nameof(NetworkTitle), nameof(NetworkStatusText), nameof(NetworkGlyph), nameof(NetworkOk),
                 })
        {
            Raise(name);
        }
    }
}

/// <summary>One row of the output device list.</summary>
internal sealed class AudioDeviceItem(AudioDevice device)
{
    public AudioDevice Device { get; } = device;

    public string Name => Device.Name;

    public bool IsDefault => Device.IsDefault;
}
