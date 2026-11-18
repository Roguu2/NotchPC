using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Platform.Audio;

namespace NotchPC.App.Audio;

/// <summary>
/// The audio tab: output device, its volume and the per-app mixer. Looked at only while the tab is open
/// (one read per second, ~60 ms on a pool thread); nothing runs when it is closed.
/// </summary>
internal sealed class AudioMixerViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan HoldAfterChange = TimeSpan.FromSeconds(1.5);

    private readonly DispatcherTimer _poll;
    private readonly Dictionary<string, DateTime> _changedAt = new(StringComparer.OrdinalIgnoreCase);
    private DefaultAudioEndpoint? _output;
    private bool _reading;

    public AudioMixerViewModel()
    {
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => _ = RefreshAppsAsync();
        Loc.CultureChanged += (_, _) =>
        {
            foreach (var app in Apps)
            {
                app.RaiseName();
            }
        };
    }

    public ObservableCollection<OutputDeviceRow> Devices { get; } = [];

    public ObservableCollection<MixerApp> Apps { get; } = [];

    public bool HasOutput => _output?.IsAvailable == true;

    public double Volume
    {
        get => (_output?.Volume ?? 0) * 100;
        set => _output?.SetVolume((float)(Math.Clamp(value, 0, 100) / 100));
    }

    public string VolumeText => $"{Math.Round(Volume)}%";

    public bool IsMuted => _output?.IsMuted ?? false;

    public string VolumeGlyph => IsMuted ? "" : "";

    public bool HasApps => Apps.Count > 0;

    /// <summary>The tab was opened or closed.</summary>
    public void SetVisible(bool visible)
    {
        if (visible)
        {
            if (_output is null)
            {
                try
                {
                    _output = new DefaultAudioEndpoint(AudioFlow.Output);
                    _output.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(OnOutputChanged);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    _output = null;
                }
            }

            RefreshDevices();
            RaiseVolume();
            _ = RefreshAppsAsync();
            _poll.Start();
        }
        else
        {
            _poll.Stop();
            _output?.Dispose(); // no device callbacks while the tab is closed
            _output = null;
        }
    }

    public void ToggleMute() => _output?.SetMuted(!IsMuted);

    public void SelectDevice(OutputDeviceRow row)
    {
        if (!row.IsDefault && !AudioDevices.SetDefault(row.Id))
        {
            AppLog.Info("Switching the output device failed");
        }

        RefreshDevices();
        _ = RefreshAppsAsync();
    }

    public void SetAppVolume(MixerApp app, double percent)
    {
        _changedAt[app.Key] = DateTime.UtcNow;
        var level = (float)(Math.Clamp(percent, 0, 100) / 100);
        _ = Task.Run(() => AudioSessions.SetVolume(app.Key, level));
    }

    public void ToggleAppMute(MixerApp app)
    {
        _changedAt[app.Key] = DateTime.UtcNow;
        var muted = !app.Muted;
        app.Update(app.Volume, muted, app.IsActive);
        _ = Task.Run(() => AudioSessions.SetMute(app.Key, muted));
    }

    public void Dispose()
    {
        _poll.Stop();
        _output?.Dispose();
    }

    private void OnOutputChanged()
    {
        RaiseVolume();
        if (Devices.FirstOrDefault(d => d.IsDefault)?.Id != _output?.DeviceId)
        {
            RefreshDevices();
            _ = RefreshAppsAsync(); // the mixer belongs to the default device
        }
    }

    private void RaiseVolume()
    {
        Raise(nameof(HasOutput));
        Raise(nameof(Volume));
        Raise(nameof(VolumeText));
        Raise(nameof(IsMuted));
        Raise(nameof(VolumeGlyph));
    }

    private void RefreshDevices()
    {
        var list = AudioDevices.List(AudioFlow.Output);
        Devices.Clear();
        foreach (var d in list)
        {
            Devices.Add(new OutputDeviceRow(d.Id, d.Name, d.IsDefault));
        }
    }

    private async Task RefreshAppsAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        try
        {
            var list = await Task.Run(AudioSessions.List).ConfigureAwait(true);
            Merge(list);
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>Updates rows in place (a slider being dragged keeps its row and value).</summary>
    private void Merge(IReadOnlyList<AudioApp> list)
    {
        var now = DateTime.UtcNow;
        var keys = list.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = Apps.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(Apps[i].Key))
            {
                Apps.RemoveAt(i);
            }
        }

        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            var row = Apps.FirstOrDefault(r => string.Equals(r.Key, a.Key, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new MixerApp(this, a);
                Apps.Insert(Math.Min(i, Apps.Count), row);
                continue;
            }

            var held = _changedAt.TryGetValue(a.Key, out var at) && now - at < HoldAfterChange;
            if (!held)
            {
                row.Update(a.Volume * 100, a.Muted, a.IsActive);
            }

            var index = Apps.IndexOf(row);
            if (index != i && i < Apps.Count)
            {
                Apps.Move(index, i);
            }
        }

        Raise(nameof(HasApps));
    }
}

/// <summary>One output device in the list.</summary>
internal sealed record OutputDeviceRow(string Id, string Name, bool IsDefault);

/// <summary>One app in the mixer.</summary>
internal sealed class MixerApp : ObservableObject
{
    private readonly AudioMixerViewModel _owner;
    private double _volume;
    private bool _muted;
    private bool _active;
    private ImageSource? _icon;

    public MixerApp(AudioMixerViewModel owner, AudioApp app)
    {
        _owner = owner;
        Key = app.Key;
        AppName = app.Name;
        IsSystemSounds = app.IsSystemSounds;
        _volume = app.Volume * 100;
        _muted = app.Muted;
        _active = app.IsActive;
        if (app.ExePath is { } path)
        {
            _ = LoadIconAsync(path);
        }
    }

    public string Key { get; }

    public string AppName { get; }

    public bool IsSystemSounds { get; }

    public string Name => IsSystemSounds ? Loc.Get("Audio.SystemSounds") : AppName;

    public ImageSource? Icon => _icon;

    public bool HasIcon => _icon is not null;

    public string Glyph => IsSystemSounds ? "" : "";

    /// <summary>0–100; set by the slider.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (Math.Abs(value - _volume) < 0.5)
            {
                return;
            }

            _volume = value;
            Raise(nameof(Volume));
            Raise(nameof(VolumeText));
            _owner.SetAppVolume(this, value);
        }
    }

    public string VolumeText => $"{Math.Round(_volume)}%";

    public bool Muted => _muted;

    public string MuteGlyph => _muted ? "" : "";

    /// <summary>Playing right now (shown brighter).</summary>
    public bool IsActive => _active;

    public void Update(double volume, bool muted, bool active)
    {
        if (Math.Abs(volume - _volume) >= 0.5)
        {
            _volume = volume;
            Raise(nameof(Volume));
            Raise(nameof(VolumeText));
        }

        if (muted != _muted)
        {
            _muted = muted;
            Raise(nameof(Muted));
            Raise(nameof(MuteGlyph));
        }

        if (active != _active)
        {
            _active = active;
            Raise(nameof(IsActive));
        }
    }

    public void RaiseName() => Raise(nameof(Name));

    private async Task LoadIconAsync(string path)
    {
        _icon = await ShellImageLoader.LoadAsync(path, 32, iconOnly: true).ConfigureAwait(true);
        Raise(nameof(Icon));
        Raise(nameof(HasIcon));
    }
}
