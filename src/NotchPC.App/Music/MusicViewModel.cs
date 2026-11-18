using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Core.Lyrics;
using NotchPC.Core.Music;
using NotchPC.Platform.Audio;
using NotchPC.Platform.Media;
using MediaClock = NotchPC.Core.Music.MediaClock;

namespace NotchPC.App.Music;

public enum LyricsState
{
    NoMedia,
    Loading,
    Synced,
    Plain,
    NotFound,
    Instrumental,
    Error,
}

public sealed class LyricLineViewModel(string text) : INotifyPropertyChanged
{
    private bool _isCurrent;
    private bool _isPast;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text { get; } = string.IsNullOrWhiteSpace(text) ? "♪" : text;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }
    }

    public bool IsPast
    {
        get => _isPast;
        set
        {
            if (_isPast != value)
            {
                _isPast = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPast)));
            }
        }
    }
}

/// <summary>
/// State behind the music panel, the pill mini-player and the lyrics card.
/// Everything is event-driven; the position ticker only runs while something showing the position is visible
/// and music is playing.
/// </summary>
internal sealed class MusicViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MediaSessionService _media;
    private readonly DefaultAudioEndpoint? _output;
    private readonly DefaultAudioEndpoint? _mic;
    private readonly LrclibClient _lyricsClient;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _ticker;
    private readonly DispatcherTimer _thumbnailRetry;
    private MediaSnapshot? _snapshot;
    private string? _coverKey;
    private string? _lyricsKey;
    private LyricsDocument? _lyrics;
    private CancellationTokenSource? _lyricsCts;
    private bool _panelVisible;
    private bool _lyricsVisible;
    private ImageSource? _cover;
    private Color? _accent;
    private LyricsState _lyricsState = LyricsState.NoMedia;
    private string _plainLyrics = "";
    private int _currentLine = -1;
    private double _progress;
    private string _positionText = "0:00";
    private string _remainingText = "-0:00";

    public MusicViewModel(SettingsService settings, MediaSessionService media, LrclibClient lyricsClient)
    {
        _settings = settings;
        _media = media;
        _lyricsClient = lyricsClient;
        _media.Changed += (_, s) => OnMediaChanged(s);

        _output = TryCreate(AudioFlow.Output);
        _mic = TryCreate(AudioFlow.Input);
        if (_output is not null)
        {
            _output.Changed += (_, _) => Raise(nameof(Volume), nameof(IsOutputMuted), nameof(VolumeGlyph));
        }

        if (_mic is not null)
        {
            _mic.Changed += (_, _) => Raise(nameof(IsMicMuted), nameof(MicAvailable), nameof(MicGlyph), nameof(MicToolTip));
        }

        _ticker = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
        _ticker.Tick += (_, _) => UpdatePosition();
        _thumbnailRetry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _thumbnailRetry.Tick += async (_, _) =>
        {
            _thumbnailRetry.Stop();
            await LoadCoverAsync(force: true);
        };

        Loc.CultureChanged += (_, _) => Raise(nameof(ArtistText), nameof(MicToolTip));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LyricLineViewModel> LyricLines { get; } = [];

    public bool HasMedia => _snapshot is not null;

    public bool IsPlaying => _snapshot?.IsPlaying ?? false;

    public string Title => _snapshot?.Title ?? "";

    public string ArtistText => string.IsNullOrWhiteSpace(_snapshot?.Artist) ? Loc.Get("Music.UnknownArtist") : _snapshot!.Artist;

    public bool CanPlayPause => _snapshot?.CanPlayPause ?? false;

    public bool CanGoNext => _snapshot?.CanGoNext ?? false;

    public bool CanGoPrevious => _snapshot?.CanGoPrevious ?? false;

    public bool CanSeek => _snapshot?.CanSeek ?? false;

    public bool HasTimeline => _snapshot?.HasTimeline ?? false;

    public string PlayPauseGlyph => IsPlaying ? "" : "";

    public ImageSource? Cover
    {
        get => _cover;
        private set
        {
            Set(ref _cover, value);
            Raise(nameof(HasCover));
        }
    }

    public bool HasCover => _cover is not null;

    /// <summary>Accent colour from the cover, used for the panel glow.</summary>
    public Color? Accent
    {
        get => _accent;
        private set => Set(ref _accent, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        private set => Set(ref _remainingText, value);
    }

    /// <summary>0..100 (Windows master volume of the default output).</summary>
    public double Volume
    {
        get => (_output?.Volume ?? 0) * 100;
        set
        {
            if (_output is null || Math.Abs(value - Volume) < 0.5)
            {
                return;
            }

            _output.SetVolume((float)(value / 100));
            if (value > 0 && _output.IsMuted)
            {
                _output.SetMuted(false);
            }

            Raise(nameof(Volume), nameof(VolumeGlyph));
        }
    }

    public bool IsOutputMuted => _output?.IsMuted ?? false;

    public string VolumeGlyph
    {
        get
        {
            if (IsOutputMuted || Volume < 0.5)
            {
                return ""; // Mute
            }

            return Volume switch
            {
                < 34 => "", // Volume1
                < 67 => "", // Volume2
                _ => "",    // Volume3
            };
        }
    }

    public bool MicAvailable => _mic?.IsAvailable ?? false;

    public bool IsMicMuted => _mic?.IsMuted ?? false;

    public string MicGlyph => MicAvailable && IsMicMuted ? "" : ""; // MicOff / Microphone

    public string MicToolTip => !MicAvailable ? Loc.Get("Music.NoMic") : Loc.Get(IsMicMuted ? "Music.MicOff" : "Music.MicOn");

    public bool LyricsOpen
    {
        get => _settings.Current.Music.LyricsOpen;
        set
        {
            if (value == LyricsOpen)
            {
                return;
            }

            _settings.Update(s => s.Music.LyricsOpen = value);
            Raise(nameof(LyricsOpen));
        }
    }

    public LyricsState LyricsState
    {
        get => _lyricsState;
        private set => Set(ref _lyricsState, value);
    }

    public string PlainLyrics
    {
        get => _plainLyrics;
        private set => Set(ref _plainLyrics, value);
    }

    /// <summary>Index into <see cref="LyricLines"/> of the line being sung, -1 before the first line.</summary>
    public int CurrentLine
    {
        get => _currentLine;
        private set => Set(ref _currentLine, value);
    }

    public async Task StartAsync()
    {
        try
        {
            await _media.StartAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            AppLog.Error("Media sessions (SMTC) unavailable", ex);
        }
    }

    /// <summary>The music tab of the expanded notch is on screen.</summary>
    public void SetPanelVisible(bool visible)
    {
        _panelVisible = visible;
        UpdateTicker();
    }

    /// <summary>The lyrics card is on screen.</summary>
    public void SetLyricsVisible(bool visible)
    {
        _lyricsVisible = visible;
        UpdateTicker();
        if (visible)
        {
            _ = LoadLyricsAsync(force: false);
        }
    }

    public Task PlayPauseAsync() => _media.PlayPauseAsync();

    public Task NextAsync() => _media.NextAsync();

    public Task PreviousAsync() => _media.PreviousAsync();

    /// <summary>Track length (zero without a timeline); the progress bar uses it for its time bubble.</summary>
    public TimeSpan Duration => _snapshot?.Duration ?? TimeSpan.Zero;

    /// <summary>Jump to a point of the track given as 0..1.</summary>
    public Task SeekAsync(double fraction) =>
        _snapshot is { CanSeek: true } s
            ? SeekToAsync(TimeSpan.FromTicks((long)(s.Duration.Ticks * Math.Clamp(fraction, 0, 1))))
            : Task.CompletedTask;

    /// <summary>Jump forward (positive) or back (negative) from the current position.</summary>
    public Task SeekByAsync(TimeSpan delta) =>
        _snapshot is { CanSeek: true } s
            ? SeekToAsync(MediaClock.PositionAt(s, DateTimeOffset.UtcNow) + delta)
            : Task.CompletedTask;

    private async Task SeekToAsync(TimeSpan target)
    {
        if (_snapshot is not { CanSeek: true } s)
        {
            return;
        }

        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }
        else if (target > s.Duration)
        {
            target = s.Duration;
        }

        var accepted = await _media.SeekAsync(s.TimelineStart + target);
        if (!accepted)
        {
            AppLog.Info($"Player {s.SourceApp} refused to seek");
            return;
        }

        // Show the new position at once; the player's own timeline update may take a moment.
        if (_snapshot?.TrackKey == s.TrackKey)
        {
            _snapshot = _snapshot with { Position = target, PositionUpdatedAt = DateTimeOffset.UtcNow };
            UpdatePosition();
        }
    }

    public void ToggleOutputMute() => _output?.SetMuted(!IsOutputMuted);

    public void ToggleMic()
    {
        if (MicAvailable)
        {
            _mic!.SetMuted(!IsMicMuted);
        }
    }

    public Task RetryLyricsAsync() => LoadLyricsAsync(force: true);

    public void Dispose()
    {
        _ticker.Stop();
        _thumbnailRetry.Stop();
        _lyricsCts?.Cancel();
        _lyricsCts?.Dispose();
        _output?.Dispose();
        _mic?.Dispose();
        _media.Dispose();
    }

    private static DefaultAudioEndpoint? TryCreate(AudioFlow flow)
    {
        try
        {
            return new DefaultAudioEndpoint(flow);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            AppLog.Error($"Core Audio ({flow}) unavailable", ex);
            return null;
        }
    }

    private void OnMediaChanged(MediaSnapshot? snapshot)
    {
        var trackChanged = snapshot?.TrackKey != _snapshot?.TrackKey;
        _snapshot = snapshot;

        Raise(nameof(HasMedia), nameof(IsPlaying), nameof(Title), nameof(ArtistText), nameof(CanPlayPause),
              nameof(CanGoNext), nameof(CanGoPrevious), nameof(CanSeek), nameof(HasTimeline), nameof(PlayPauseGlyph),
              nameof(Duration));
        UpdatePosition();
        UpdateTicker();

        if (trackChanged)
        {
            _ = LoadCoverAsync(force: false);
            if (_lyricsVisible)
            {
                _ = LoadLyricsAsync(force: false);
            }
        }

        if (snapshot is null)
        {
            ShowLyrics(null, LyricsState.NoMedia);
        }
    }

    private void UpdateTicker()
    {
        var run = (_panelVisible || _lyricsVisible) && IsPlaying;

        // Lyrics need sub-second precision; the panel only shows whole seconds.
        var interval = TimeSpan.FromMilliseconds(_lyricsVisible ? 200 : 1000);
        if (_ticker.Interval != interval)
        {
            _ticker.Interval = interval;
        }

        if (run && !_ticker.IsEnabled)
        {
            _ticker.Start();
        }
        else if (!run && _ticker.IsEnabled)
        {
            _ticker.Stop();
        }
    }

    private void UpdatePosition()
    {
        if (_snapshot is not { } s)
        {
            Progress = 0;
            PositionText = "0:00";
            RemainingText = "-0:00";
            CurrentLine = -1;
            return;
        }

        var position = MediaClock.PositionAt(s, DateTimeOffset.UtcNow);
        PositionText = MediaClock.Format(position);
        if (s.HasTimeline)
        {
            Progress = position.TotalMilliseconds / s.Duration.TotalMilliseconds;
            RemainingText = "-" + MediaClock.Format(s.Duration - position);
        }
        else
        {
            Progress = 0;
            RemainingText = "";
        }

        if (_lyrics is { Kind: LyricsKind.Synced } doc && _lyricsKey == s.TrackKey)
        {
            // Show each line slightly early: it reads better than lagging behind the singer.
            SetCurrentLine(doc.LineIndexAt(position + TimeSpan.FromMilliseconds(250)));
        }
    }

    private void SetCurrentLine(int index)
    {
        if (index == CurrentLine)
        {
            return;
        }

        for (var i = 0; i < LyricLines.Count; i++)
        {
            LyricLines[i].IsCurrent = i == index;
            LyricLines[i].IsPast = i < index;
        }

        CurrentLine = index;
    }

    private async Task LoadCoverAsync(bool force)
    {
        var key = _snapshot?.TrackKey;
        if (!force && key == _coverKey)
        {
            return;
        }

        _coverKey = key;
        if (key is null)
        {
            Cover = null;
            Accent = null;
            return;
        }

        if (!force)
        {
            Cover = null;
            Accent = null;
        }

        var bytes = await _media.GetThumbnailAsync();
        if (key != _snapshot?.TrackKey)
        {
            return; // track changed while loading
        }

        if (bytes is null)
        {
            if (!force)
            {
                _thumbnailRetry.Start(); // some players publish the cover a moment after the title
            }

            return;
        }

        try
        {
            var image = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 160;
                image.StreamSource = stream;
                image.EndInit();
            }

            image.Freeze();
            Cover = image;
            Accent = ComputeAccent(image);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException)
        {
            AppLog.Error("Cover decode failed", ex);
        }
    }

    private static Color? ComputeAccent(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var small = new TransformedBitmap(bgra, new ScaleTransform(12.0 / bgra.PixelWidth, 12.0 / bgra.PixelHeight));
        var stride = small.PixelWidth * 4;
        var pixels = new byte[stride * small.PixelHeight];
        small.CopyPixels(pixels, stride, 0);
        return CoverColor.Accent(pixels) is { } c ? Color.FromRgb(c.R, c.G, c.B) : null;
    }

    private async Task LoadLyricsAsync(bool force)
    {
        if (_snapshot is not { } s)
        {
            ShowLyrics(null, LyricsState.NoMedia);
            return;
        }

        if (!force && s.TrackKey == _lyricsKey && LyricsState != LyricsState.Error)
        {
            return;
        }

        _lyricsCts?.Cancel();
        _lyricsCts?.Dispose();
        var cts = _lyricsCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _lyricsKey = s.TrackKey;
        ShowLyrics(null, LyricsState.Loading);

        try
        {
            var doc = await _lyricsClient.GetAsync(s.Artist, s.Title, s.Album, s.Duration, cts.Token);
            if (cts.IsCancellationRequested || _lyricsKey != _snapshot?.TrackKey)
            {
                return;
            }

            ShowLyrics(doc, doc.Kind switch
            {
                LyricsKind.Synced => LyricsState.Synced,
                LyricsKind.Plain => LyricsState.Plain,
                LyricsKind.Instrumental => LyricsState.Instrumental,
                _ => LyricsState.NotFound,
            });
        }
        catch (LyricsUnavailableException ex)
        {
            AppLog.Error("Lyrics lookup failed", ex);
            if (!cts.IsCancellationRequested)
            {
                ShowLyrics(null, LyricsState.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer track.
        }
    }

    private void ShowLyrics(LyricsDocument? doc, LyricsState state)
    {
        _lyrics = doc;
        LyricLines.Clear();
        _currentLine = -1;
        if (doc is { Kind: LyricsKind.Synced })
        {
            foreach (var line in doc.Lines)
            {
                LyricLines.Add(new LyricLineViewModel(line.Text));
            }
        }

        PlainLyrics = doc?.Kind == LyricsKind.Plain ? doc.PlainText : "";
        LyricsState = state;
        Raise(nameof(CurrentLine));
        UpdatePosition();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Raise(params string[] names)
    {
        foreach (var n in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}
