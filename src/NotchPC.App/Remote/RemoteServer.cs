using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using NotchPC.App.Games;
using NotchPC.App.Infrastructure;
using NotchPC.App.Music;
using NotchPC.App.Notifications;
using NotchPC.App.Clipboard;
using NotchPC.App.Shelf;
using NotchPC.Core.Clipboard;
using NotchPC.Core.Localization;
using NotchPC.Core.Remote;
using NotchPC.Core.Settings;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Remote;

/// <summary>
/// The phone remote: a small web server in the home network (Kestrel). The phone opens the page from the QR code,
/// pairs once (one-time code from the QR → its own device token) and then talks over a WebSocket. Only local
/// network addresses are served; every message needs the paired token. Runs only while turned on in the settings.
/// </summary>
internal sealed class RemoteServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(10);
    private const long MaxUpload = 4L * 1024 * 1024 * 1024; // 4 GB per file
    private const int MaxClipText = 4000; // per clipboard item sent to the phone

    private readonly SettingsService _settings;
    private readonly MusicViewModel _music;
    private readonly GameOverlayController _overlay;
    private readonly NotificationHub _hub;
    private readonly ShelfViewModel _shelf;
    private readonly ClipboardService _clipboard;
    private readonly Lock _uploadGate = new();
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly PairingCode _pairing = new(TimeSpan.FromMinutes(10));
    private readonly Lock _pairingGate = new();
    private readonly System.Windows.Threading.DispatcherTimer _musicDebounce;
    private readonly Dictionary<string, byte[]> _files = [];
    private WebApplication? _app;
    private int _port;
    private bool _starting;

    public RemoteServer(SettingsService settings, MusicViewModel music, GameOverlayController overlay, NotificationHub hub, ShelfViewModel shelf, ClipboardService clipboard)
    {
        _settings = settings;
        _music = music;
        _overlay = overlay;
        _hub = hub;
        _shelf = shelf;
        _clipboard = clipboard;
        _shelf.Items.CollectionChanged += (_, _) =>
        {
            if (!_clients.IsEmpty)
            {
                Broadcast(FilesMessage(), _ => true);
            }
        };
        _musicDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _musicDebounce.Tick += (_, _) =>
        {
            _musicDebounce.Stop();
            Broadcast(MusicMessage(), c => true);
        };
        _music.PropertyChanged += (_, e) =>
        {
            if (!_clients.IsEmpty && e.PropertyName is nameof(MusicViewModel.Title) or nameof(MusicViewModel.ArtistText)
                    or nameof(MusicViewModel.IsPlaying) or nameof(MusicViewModel.Volume) or nameof(MusicViewModel.IsOutputMuted)
                    or nameof(MusicViewModel.HasMedia))
            {
                _musicDebounce.Start();
            }
        };
        _overlay.StatsUpdated += (_, stats) => Broadcast(StatsMessage(stats), c => c.WantsStats);
        _settings.Changed += (_, _) =>
        {
            _ = ApplyAsync();
            DropUnpaired();
        };
        _ = ApplyAsync();
    }

    public event EventHandler? StateChanged;

    public bool IsRunning => _app is not null;

    /// <summary>Why the server could not start (e.g. the port is taken), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>"http://192.168.1.20:47800" (the address the phone opens), or null when off / no network.</summary>
    public string? Address => IsRunning && LanAddress() is { } ip ? $"http://{ip}:{_port}" : null;

    public int ConnectedCount => _clients.Count;

    /// <summary>The address with a fresh one-time pairing code (what the QR code contains).</summary>
    public string? PairingUrl()
    {
        if (Address is not { } address)
        {
            return null;
        }

        lock (_pairingGate)
        {
            return $"{address}/#p={_pairing.Current(DateTimeOffset.UtcNow)}";
        }
    }

    /// <summary>A new code (the QR changes); the old one stops working.</summary>
    public void RenewPairingCode()
    {
        lock (_pairingGate)
        {
            _pairing.Invalidate();
        }
    }

    public void Dispose()
    {
        _musicDebounce.Stop();
        _ = StopAsync();
    }

    /// <summary>The IPv4 address of the adapter that has the default gateway (home network), not VPN / virtual ones.</summary>
    internal static IPAddress? LanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                            && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && LocalNetwork.IsLocal(a) && !IPAddress.IsLoopback(a));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private async Task ApplyAsync()
    {
        var s = _settings.Current.Remote;
        if (s.Enabled && IsRunning && s.Port != _port)
        {
            await StopAsync().ConfigureAwait(true);
        }

        if (s.Enabled && !IsRunning && !_starting)
        {
            await StartAsync(s.Port).ConfigureAwait(true);
        }
        else if (!s.Enabled && IsRunning)
        {
            await StopAsync().ConfigureAwait(true);
        }
    }

    private async Task StartAsync(int port)
    {
        _starting = true;
        Error = null;
        try
        {
            LoadFiles();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.ListenAnyIP(port);
                o.Limits.MaxRequestBodySize = 16 * 1024;
                o.AddServerHeader = false;
            });
            var app = builder.Build();
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
            app.Use(async (ctx, next) =>
            {
                // The remote is for the home network only (no port forwarding from the internet).
                if (!LocalNetwork.IsLocal(ctx.Connection.RemoteIpAddress))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                ctx.Response.Headers.XContentTypeOptions = "nosniff";
                ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
                ctx.Response.Headers.CacheControl = "no-cache";
                await next(ctx).ConfigureAwait(false);
            });
            app.MapGet("/", ctx => Serve(ctx, "index.html", "text/html; charset=utf-8"));
            app.MapGet("/app.js", ctx => Serve(ctx, "app.js", "text/javascript; charset=utf-8"));
            app.MapGet("/app.css", ctx => Serve(ctx, "app.css", "text/css; charset=utf-8"));
            app.MapGet("/icon.png", ctx => Serve(ctx, "icon.png", "image/png"));
            app.MapGet("/manifest.webmanifest", ctx => Serve(ctx, "manifest.webmanifest", "application/manifest+json"));
            app.MapPost("/api/pair", PairAsync);
            app.MapPost("/api/upload", UploadAsync);
            app.MapGet("/api/file/{id}", DownloadAsync);
            app.Map("/ws", SocketAsync);
            await app.StartAsync().ConfigureAwait(true);
            _app = app;
            _port = port;
            AppLog.Info($"Phone remote listening on port {port}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            AppLog.Error("Phone remote could not start", ex);
        }
        finally
        {
            _starting = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task StopAsync()
    {
        if (_app is not { } app)
        {
            return;
        }

        _app = null;
        foreach (var c in _clients.Values)
        {
            c.Abort();
        }

        try
        {
            await app.StopAsync().ConfigureAwait(true);
            await app.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }

        AppLog.Info("Phone remote stopped");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadFiles()
    {
        if (_files.Count > 0)
        {
            return;
        }

        var asm = typeof(RemoteServer).Assembly;
        foreach (var name in new[] { "index.html", "app.js", "app.css", "manifest.webmanifest" })
        {
            using var stream = asm.GetManifestResourceStream("NotchPC.Remote." + name)
                               ?? throw new InvalidOperationException("Missing remote page file " + name);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _files[name] = ms.ToArray();
        }

        _files["icon.png"] = IconPng();
    }

    /// <summary>The app icon as a 192 px PNG (home-screen icon on the phone).</summary>
    private static byte[] IconPng()
    {
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/NotchPC.ico"),
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(source,
            new System.Windows.Media.ScaleTransform(192.0 / source.PixelWidth, 192.0 / source.PixelHeight));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private Task Serve(HttpContext ctx, string name, string contentType)
    {
        ctx.Response.ContentType = contentType;
        if (name == "index.html")
        {
            ctx.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; connect-src 'self' ws: ; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
        }

        return ctx.Response.Body.WriteAsync(_files[name]).AsTask();
    }

    /// <summary>POST {code} → {token}: the one-time code from the QR becomes this phone's own token.</summary>
    private async Task PairAsync(HttpContext ctx)
    {
        PairRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<PairRequest>(ctx.Request.Body, Json).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        bool ok;
        lock (_pairingGate)
        {
            ok = _pairing.TryUse(request?.Code, DateTimeOffset.UtcNow);
        }

        if (!ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var token = RemoteSecrets.NewToken();
        var device = new PairedDevice
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = LocalNetwork.DeviceName(ctx.Request.Headers.UserAgent.ToString()),
            TokenHash = RemoteSecrets.Hash(token),
            PairedAt = DateTimeOffset.Now,
        };
        await OnUi(() =>
        {
            _settings.Update(s => s.Remote.Devices.Add(device));
            _hub.Post(Loc.Get("Remote.Paired.Title"), Loc.Format("Remote.Paired.Body", device.Name), "", Loc.Get("Tab.phone"));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(false);
        AppLog.Info("Phone remote: new device paired");
        await ctx.Response.WriteAsJsonAsync(new { token, name = device.Name }).ConfigureAwait(false);
    }

    private async Task SocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var authTimeout = new CancellationTokenSource(AuthTimeout);
        string? first;
        try
        {
            first = await ReceiveAsync(socket, authTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var device = first is not null && RemoteCommand.Parse(first) is RemoteCommand.Auth auth
            ? await OnUi(() => _settings.Current.Remote.Devices.FirstOrDefault(d => RemoteSecrets.Matches(auth.Token, d.TokenHash))).ConfigureAwait(false)
            : null;
        if (device is null)
        {
            await SendRawAsync(socket, JsonSerializer.Serialize(new { t = "unpaired" })).ConfigureAwait(false);
            await CloseAsync(socket).ConfigureAwait(false);
            return;
        }

        var client = new Client(device.Id, socket);
        _clients[client.Id] = client;
        await OnUi(() =>
        {
            _settings.Update(s =>
            {
                if (s.Remote.Devices.FirstOrDefault(d => d.Id == device.Id) is { } d)
                {
                    d.LastSeen = DateTimeOffset.Now;
                }
            });
            StateChanged?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(false);

        try
        {
            await client.SendAsync(await OnUi(HelloMessage).ConfigureAwait(false)).ConfigureAwait(false);
            while (socket.State == WebSocketState.Open)
            {
                var text = await ReceiveAsync(socket, CancellationToken.None).ConfigureAwait(false);
                if (text is null)
                {
                    break;
                }

                if (RemoteCommand.Parse(text) is { } command)
                {
                    await HandleAsync(client, command).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            if (client.WantsStats)
            {
                await OnUi(() => _overlay.ReleaseStats(client)).ConfigureAwait(false);
            }

            await OnUi(() => StateChanged?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
            client.Dispose();
        }
    }

    private async Task HandleAsync(Client client, RemoteCommand command)
    {
        switch (command)
        {
            case RemoteCommand.Move m:
                RemoteInput.Move(m.Dx, m.Dy);
                break;
            case RemoteCommand.Scroll s:
                RemoteInput.Scroll(s.Dy, s.Dx);
                break;
            case RemoteCommand.Click c:
                RemoteInput.Click(c.Button, c.IsDouble);
                break;
            case RemoteCommand.Press p:
                RemoteInput.Button(p.Button, p.Down);
                break;
            case RemoteCommand.Text t:
                RemoteInput.Type(t.Value);
                break;
            case RemoteCommand.Backspace b:
                RemoteInput.Keys([0x08], b.Count);
                break;
            case RemoteCommand.Key k:
                RemoteInput.Keys(RemoteKeys.Keys(k.Name));
                break;
            case RemoteCommand.Media media:
                await OnUi(() => Media(media.Action)).ConfigureAwait(false);
                break;
            case RemoteCommand.Volume v:
                await OnUi(() => _music.Volume = v.Percent).ConfigureAwait(false);
                break;
            case RemoteCommand.Launch l:
                await OnUi(() => Launch(l.Index)).ConfigureAwait(false);
                break;
            case RemoteCommand.Power p:
                await OnUi(() => Power(p.Action)).ConfigureAwait(false);
                break;
            case RemoteCommand.Stats s when s.On != client.WantsStats:
                client.WantsStats = s.On;
                await OnUi(() =>
                {
                    if (s.On)
                    {
                        _overlay.AcquireStats(client);
                    }
                    else
                    {
                        _overlay.ReleaseStats(client);
                    }
                }).ConfigureAwait(false);
                break;
            case RemoteCommand.ToggleOverlay:
                await OnUi(_overlay.ToggleForForeground).ConfigureAwait(false);
                break;
            case RemoteCommand.ListFiles:
                await client.SendAsync(await OnUi(FilesMessage).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case RemoteCommand.ListClips:
                await client.SendAsync(await OnUi(ClipsMessage).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case RemoteCommand.SetClipboard c:
                var ok = await OnUi(() => SetClipboard(c.Value)).ConfigureAwait(false);
                await client.SendAsync(JsonSerializer.Serialize(new { t = "clipset", ok }, Json)).ConfigureAwait(false);
                break;
        }
    }

    private void Media(string action)
    {
        switch (action)
        {
            case "playpause":
                _ = _music.PlayPauseAsync();
                break;
            case "next":
                _ = _music.NextAsync();
                break;
            case "prev":
                _ = _music.PreviousAsync();
                break;
            case "volup":
                _music.Volume = Math.Min(100, _music.Volume + 5);
                break;
            case "voldown":
                _music.Volume = Math.Max(0, _music.Volume - 5);
                break;
            case "mute":
                _music.ToggleOutputMute();
                break;
        }
    }

    /// <summary>Only shortcuts from the user's own list (by position): the phone cannot start arbitrary programs.</summary>
    private void Launch(int index)
    {
        var shortcuts = _settings.Current.Apps.Shortcuts ?? [];
        if (index >= shortcuts.Count)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(shortcuts[index].Target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            AppLog.Error("Phone remote: starting a shortcut failed", ex);
        }
    }

    private void Power(string action)
    {
        var delay = _settings.Current.Remote.PowerDelaySeconds;
        AppLog.Info($"Phone remote: power {action}");
        switch (action)
        {
            case "lock":
                PowerActions.Lock();
                break;
            case "sleep":
                PowerActions.Sleep();
                break;
            case "shutdown":
                if (PowerActions.Shutdown(delay))
                {
                    _hub.Post(Loc.Get("Remote.Shutdown.Title"), Loc.Format("Remote.Shutdown.Body", delay), "", Loc.Get("Tab.phone"));
                }

                break;
            case "restart":
                if (PowerActions.Restart(delay))
                {
                    _hub.Post(Loc.Get("Remote.Restart.Title"), Loc.Format("Remote.Shutdown.Body", delay), "", Loc.Get("Tab.phone"));
                }

                break;
            case "cancel":
                PowerActions.Cancel();
                break;
        }
    }

    /// <summary>A paired phone's request (token in the X-Token header) while files and clipboard are allowed.</summary>
    private async Task<bool> AuthorizedAsync(HttpContext ctx)
    {
        var token = ctx.Request.Headers["X-Token"].ToString();
        return token.Length > 0 && await OnUi(() =>
            _settings.Current.Remote.FilesAndClipboard &&
            _settings.Current.Remote.Devices.Any(d => RemoteSecrets.Matches(token, d.TokenHash))).ConfigureAwait(false);
    }

    /// <summary>
    /// POST a file from the phone (body = the file, X-File-Name = its name, URL-encoded). It is saved in
    /// Downloads\NotchPC under a safe, unique name and put on the shelf.
    /// </summary>
    private async Task UploadAsync(HttpContext ctx)
    {
        if (!await AuthorizedAsync(ctx).ConfigureAwait(false))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxUpload;
        }

        var name = RemoteFiles.SafeName(Uri.UnescapeDataString(ctx.Request.Headers["X-File-Name"].ToString()), "file");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NotchPC");
        string path;
        string part;
        FileStream stream;
        try
        {
            Directory.CreateDirectory(dir);
            lock (_uploadGate)
            {
                name = RemoteFiles.Unique(name, n => File.Exists(Path.Combine(dir, n)) || File.Exists(Path.Combine(dir, n + ".part")));
                path = Path.Combine(dir, name);
                part = path + ".part";
                stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Phone remote: cannot save an incoming file", ex);
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await ctx.Request.Body.CopyToAsync(stream, ctx.RequestAborted).ConfigureAwait(false);
            }

            File.Move(part, path);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException or UnauthorizedAccessException)
        {
            AppLog.Info($"Phone remote: upload of a file failed ({ex.GetType().Name})");
            TryDelete(part);
            if (!ctx.RequestAborted.IsCancellationRequested)
            {
                ctx.Response.StatusCode = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
            }

            return;
        }

        AppLog.Info("Phone remote: file received");
        await OnUi(() =>
        {
            _shelf.Add([path]);
            _hub.Post(Loc.Get("Remote.File.Title"), name, "\uE896", Loc.Get("Tab.phone"), NotchPC.Core.Notifications.NotificationTargets.Tab(TabIds.Files));
        }).ConfigureAwait(false);
        await ctx.Response.WriteAsJsonAsync(new { name }).ConfigureAwait(false);
    }

    /// <summary>GET a file from the shelf by its id (only shelf files can be taken, never arbitrary paths).</summary>
    private async Task DownloadAsync(HttpContext ctx)
    {
        if (!await AuthorizedAsync(ctx).ConfigureAwait(false))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var id = ctx.Request.RouteValues["id"] as string;
        var path = await OnUi(() => _shelf.Items.Select(i => i.Path).FirstOrDefault(p => RemoteFiles.IdFor(p) == id)).ConfigureAwait(false);
        if (path is null || !File.Exists(path))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(Path.GetFileName(path));
        try
        {
            await ctx.Response.SendFileAsync(path, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The shelf files (not folders) the phone may take.</summary>
    private string FilesMessage()
    {
        var on = _settings.Current.Remote.FilesAndClipboard;
        var files = _shelf.Items
            .Where(i => on && File.Exists(i.Path))
            .Select(i => new { id = RemoteFiles.IdFor(i.Path), name = i.Name, size = RemoteFiles.Size(SizeOf(i.Path), Loc.Culture) })
            .ToList();
        return JsonSerializer.Serialize(new { t = "files", on, files }, Json);
    }

    /// <summary>
    /// Recent clipboard texts, sent only when the phone asks (opens its files tab or taps refresh). From the
    /// clipboard history when it is on (which already skips what password managers mark), else the current text.
    /// </summary>
    private string ClipsMessage()
    {
        var on = _settings.Current.Remote.FilesAndClipboard;
        var items = new List<string>();
        if (on)
        {
            if (_clipboard.Enabled)
            {
                items.AddRange(_clipboard.View(null)
                    .Where(e => e.Kind == ClipKind.Text && !string.IsNullOrWhiteSpace(e.Text))
                    .Take(8)
                    .Select(e => Clip(e.Text!)));
            }
            else if (CurrentClipboardText() is { } text)
            {
                items.Add(Clip(text));
            }
        }

        return JsonSerializer.Serialize(new { t = "clips", on, items }, Json);
    }

    private static string Clip(string text) => text.Length > MaxClipText ? text[..MaxClipText] : text;

    private static string? CurrentClipboardText()
    {
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing"))
            {
                return null;
            }

            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private bool SetClipboard(string text)
    {
        if (!_settings.Current.Remote.FilesAndClipboard)
        {
            return false;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            AppLog.Info("Phone remote: clipboard text received");
            return true;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppLog.Error("Phone remote: clipboard is busy", ex);
            return false;
        }
    }

    private string HelloMessage()
    {
        var apps = (_settings.Current.Apps.Shortcuts ?? []).Select(a => a.Name).ToList();
        return JsonSerializer.Serialize(new
        {
            t = "hello",
            lang = Loc.Culture.TwoLetterISOLanguageName,
            pc = Environment.MachineName,
            apps,
            powerDelay = _settings.Current.Remote.PowerDelaySeconds,
            files = _settings.Current.Remote.FilesAndClipboard,
            music = MusicState(),
        }, Json);
    }

    private object MusicState() => new
    {
        has = _music.HasMedia,
        title = _music.Title,
        artist = _music.HasMedia ? _music.ArtistText : "",
        playing = _music.IsPlaying,
        volume = Math.Round(_music.Volume),
        muted = _music.IsOutputMuted,
    };

    private string MusicMessage() => JsonSerializer.Serialize(new { t = "music", music = MusicState() }, Json);

    private static string StatsMessage(GameStats s) => JsonSerializer.Serialize(new
    {
        t = "stats",
        app = s.App,
        fps = s.Fps is { } f ? Math.Round(f) : (double?)null,
        cpu = s.Sensors?.CpuLoad,
        cpuTemp = s.Sensors?.CpuTemp,
        gpu = s.Sensors?.GpuLoad,
        gpuTemp = s.Sensors?.GpuTemp,
        ram = s.Sensors?.RamLoad,
        overlay = s.OverlayOn,
    }, Json);

    private void Broadcast(string message, Func<Client, bool> filter)
    {
        foreach (var c in _clients.Values)
        {
            if (filter(c))
            {
                _ = c.SendAsync(message);
            }
        }
    }

    /// <summary>A device removed in the settings is disconnected at once.</summary>
    private void DropUnpaired()
    {
        var ids = _settings.Current.Remote.Devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var c in _clients.Values.Where(c => !ids.Contains(c.DeviceId)))
        {
            _ = c.SendAsync(JsonSerializer.Serialize(new { t = "unpaired" })).ContinueWith(_ => c.Abort(), TaskScheduler.Default);
        }
    }

    private static Task OnUi(Action action) => Application.Current.Dispatcher.InvokeAsync(action).Task;

    private static Task<T> OnUi<T>(Func<T> func) => Application.Current.Dispatcher.InvokeAsync(func).Task;

    private static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
            if (ms.Length > 96 * 1024)
            {
                return null; // nothing the page sends is this big (clipboard text is the largest)
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
    }

    private static Task SendRawAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task CloseAsync(WebSocket socket)
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unpaired", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
        }
    }

    private sealed record PairRequest(string? Code);

    private sealed class Client(string deviceId, WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public Guid Id { get; } = Guid.NewGuid();

        public string DeviceId { get; } = deviceId;

        public bool WantsStats { get; set; }

        public async Task SendAsync(string text)
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                await _send.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return; // disconnected meanwhile
            }

            try
            {
                await SendRawAsync(socket, text).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
            }
            finally
            {
                try
                {
                    _send.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose() => _send.Dispose();

        public void Abort()
        {
            try
            {
                socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
