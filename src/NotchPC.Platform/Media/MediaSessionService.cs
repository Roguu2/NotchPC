using NotchPC.Core.Music;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NotchPC.Platform.Media;

/// <summary>
/// Wraps Windows' System Media Transport Controls (the same sessions the volume flyout shows: Spotify,
/// browsers, Media Player …). Purely event-driven; <see cref="Changed"/> is raised on the thread that created the service.
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private readonly SynchronizationContext? _context;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Lock _bindLock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public MediaSessionService()
    {
        _context = SynchronizationContext.Current;
    }

    /// <summary>New state, or null when no app is playing/paused media.</summary>
    public event EventHandler<MediaSnapshot?>? Changed;

    public MediaSnapshot? Current { get; private set; }

    /// <summary>Optional diagnostics sink.</summary>
    public Action<string, Exception?>? Trace { get; set; }

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => _ = RebindAsync();
        _manager.SessionsChanged += (_, _) => _ = RebindAsync();
        await RebindAsync().ConfigureAwait(false);
    }

    public Task PlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync().AsTask());

    public Task NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());

    public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());

    /// <summary>Asks the player to jump to <paramref name="position"/> (absolute timeline time). True if the player accepted.</summary>
    public Task<bool> SeekAsync(TimeSpan position) => Run(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

    /// <summary>Album art of the current track as encoded image bytes (PNG/JPEG), or null.</summary>
    public async Task<byte[]?> GetThumbnailAsync()
    {
        var session = _session;
        if (session is null)
        {
            return null;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props?.Thumbnail is not { } thumbRef)
            {
                return null;
            }

            using var stream = await thumbRef.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 16 * 1024 * 1024)
            {
                return null;
            }

            var buffer = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Trace?.Invoke("Thumbnail read failed", ex);
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Unhook(_session);
        _session = null;
        _refreshGate.Dispose();
    }

    private async Task<bool> Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        if (_session is not { } s)
        {
            return false;
        }

        try
        {
            return await action(s).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Trace?.Invoke("Media command failed", ex);
            return false;
        }
    }

    private async Task RebindAsync()
    {
        if (_disposed || _manager is null)
        {
            return;
        }

        GlobalSystemMediaTransportControlsSession? next;
        try
        {
            next = await PickSessionAsync(_manager).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Trace?.Invoke("Media session enumeration failed", ex);
            return;
        }

        // Session events arrive on thread-pool threads; swap the subscription atomically.
        lock (_bindLock)
        {
            if (!ReferenceEquals(next, _session))
            {
                Unhook(_session);
                _session = next;
                if (next is not null)
                {
                    next.MediaPropertiesChanged += OnSessionChanged;
                    next.PlaybackInfoChanged += OnSessionChanged;
                    next.TimelinePropertiesChanged += OnSessionChanged;
                }
            }
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Which session to show: among sessions that are playing, music beats video (Spotify over a muted
    /// TikTok/YouTube tab), then Windows' "current" session wins. If nothing plays, Windows' current session.
    /// </summary>
    private static async Task<GlobalSystemMediaTransportControlsSession?> PickSessionAsync(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var current = manager.GetCurrentSession();
        var playing = manager.GetSessions().Where(IsPlaying).ToList();
        if (playing.Count == 0)
        {
            return current;
        }

        if (playing.Count == 1)
        {
            return playing[0];
        }

        // Check Windows' current session first so it wins ties.
        var currentId = current?.SourceAppUserModelId;
        foreach (var s in playing.OrderByDescending(s => s.SourceAppUserModelId == currentId))
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (props?.PlaybackType == Windows.Media.MediaPlaybackType.Music)
            {
                return s;
            }
        }

        return playing.FirstOrDefault(s => s.SourceAppUserModelId == currentId) ?? playing[0];

        static bool IsPlaying(GlobalSystemMediaTransportControlsSession s) =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    private void Unhook(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s is null)
        {
            return;
        }

        s.MediaPropertiesChanged -= OnSessionChanged;
        s.PlaybackInfoChanged -= OnSessionChanged;
        s.TimelinePropertiesChanged -= OnSessionChanged;
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        // Play/pause in another app may make it the better session; re-pick on playback changes.
        _ = args is PlaybackInfoChangedEventArgs ? RebindAsync() : RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = _session is { } s ? await BuildAsync(s).ConfigureAwait(false) : null;
            if (Equals(snapshot, Current))
            {
                return;
            }

            Current = snapshot;
            Publish(snapshot);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Session went away mid-read; the SessionsChanged event follows.
            Trace?.Invoke("Media session read failed", ex);
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (!_disposed)
            {
                _refreshGate.Release();
            }
        }
    }

    private static async Task<MediaSnapshot?> BuildAsync(GlobalSystemMediaTransportControlsSession s)
    {
        var props = await s.TryGetMediaPropertiesAsync();
        var playback = s.GetPlaybackInfo();
        var timeline = s.GetTimelineProperties();
        var controls = playback?.Controls;

        var title = props?.Title ?? "";
        if (string.IsNullOrWhiteSpace(title))
        {
            return null; // e.g. a browser tab with an idle media element
        }

        var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;
        var position = timeline is null ? TimeSpan.Zero : timeline.Position - timeline.StartTime;

        return new MediaSnapshot
        {
            SourceApp = s.SourceAppUserModelId ?? "",
            Title = title.Trim(),
            Artist = (props?.Artist ?? props?.AlbumArtist ?? "").Trim(),
            Album = (props?.AlbumTitle ?? "").Trim(),
            IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            PlaybackRate = playback?.PlaybackRate ?? 1.0,
            Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
            Position = position > TimeSpan.Zero ? position : TimeSpan.Zero,
            TimelineStart = timeline?.StartTime ?? TimeSpan.Zero,
            PositionUpdatedAt = timeline?.LastUpdatedTime ?? DateTimeOffset.UtcNow,
            CanPlayPause = controls is not null && (controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled),
            CanGoNext = controls?.IsNextEnabled ?? false,
            CanGoPrevious = controls?.IsPreviousEnabled ?? false,
            CanSeek = (controls?.IsPlaybackPositionEnabled ?? false) && duration > TimeSpan.Zero,
        };
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        if (_context is null)
        {
            Changed?.Invoke(this, snapshot);
        }
        else
        {
            _context.Post(_ => Changed?.Invoke(this, snapshot), null);
        }
    }
}
