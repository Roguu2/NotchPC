using System.Collections.ObjectModel;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Core.Twitch;

namespace NotchPC.App.Widgets;

/// <summary>
/// Twitch chat widget: anonymous, read-only (no account). Connected only while the card is on screen; reconnects
/// with a growing pause. Keeps the last 100 messages; moderator deletions and timeouts are honoured.
/// </summary>
internal sealed class TwitchChatViewModel : WidgetViewModel
{
    private const int Keep = 100;

    private readonly SettingsService _settings;
    private CancellationTokenSource? _cts;
    private string? _channel;
    private ChatState _state = ChatState.Off;

    public TwitchChatViewModel(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
        _channel = TwitchIrc.NormalizeChannel(_settings.Current.Widgets.TwitchChannel);
    }

    private enum ChatState
    {
        Off,
        Connecting,
        Live,
        Retrying,
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public string Title => _channel is null ? "Twitch" : "#" + _channel;

    public bool HasChannel => _channel is not null;

    public bool IsLive => _state == ChatState.Live;

    public string StatusText => _channel is null
        ? Loc.Get("Widget.Twitch.NoChannel")
        : _state switch
        {
            ChatState.Live when Messages.Count == 0 => Loc.Get("Widget.Twitch.Quiet"),
            ChatState.Live => "",
            ChatState.Retrying => Loc.Get("Widget.Twitch.Retrying"),
            _ => Loc.Get("Widget.Twitch.Connecting"),
        };

    public bool HasStatus => StatusText.Length > 0;

    public override void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Stop();
        base.Dispose();
    }

    protected override void OnScreenChanged(bool onScreen)
    {
        if (onScreen)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    protected override void OnCulture() => RaiseState();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var channel = TwitchIrc.NormalizeChannel(_settings.Current.Widgets.TwitchChannel);
        if (channel == _channel)
        {
            return;
        }

        _channel = channel;
        Messages.Clear();
        Stop();
        if (IsOnScreen)
        {
            Start();
        }

        RaiseMany(nameof(Title), nameof(HasChannel));
        RaiseState();
    }

    private void Start()
    {
        if (_cts is not null || _channel is null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _ = RunAsync(_channel, _cts.Token);
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        SetState(ChatState.Off);
    }

    private async Task RunAsync(string channel, CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            SetState(attempt == 0 ? ChatState.Connecting : ChatState.Retrying);
            try
            {
                await SessionAsync(channel, token).ConfigureAwait(true);
                attempt = 0; // the server asked to reconnect: do it at once
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or InvalidOperationException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                attempt++;
                AppLog.Info($"Twitch chat: connection lost ({ex.GetType().Name}), retry {attempt}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 * attempt)), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One connection; returns when the server asks to reconnect.</summary>
    private async Task SessionAsync(string channel, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(TwitchIrc.Url), token).ConfigureAwait(true);
        foreach (var line in TwitchIrc.Login(channel, Random.Shared.Next(10000, 99999)))
        {
            await SendAsync(socket, line, token).ConfigureAwait(true);
        }

        var buffer = new byte[8192];
        var pending = new StringBuilder();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(true);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("closed by server");
            }

            pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            foreach (var line in pending.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                switch (TwitchIrc.Parse(line, DateTimeOffset.Now))
                {
                    case IrcEvent.Ping ping:
                        await SendAsync(socket, "PONG :" + ping.Payload, token).ConfigureAwait(true);
                        break;
                    case IrcEvent.Joined:
                        SetState(ChatState.Live);
                        break;
                    case IrcEvent.Message m:
                        Add(m.Chat);
                        break;
                    case IrcEvent.Deleted d:
                        Remove(x => x.Id == d.MessageId);
                        break;
                    case IrcEvent.Cleared c:
                        Remove(x => c.User is null || x.Login == c.User);
                        break;
                    case IrcEvent.Reconnect:
                        return;
                }
            }

            pending.Clear();
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string line, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(line + "\r\n"), WebSocketMessageType.Text, true, token);

    private void Add(ChatMessage message)
    {
        Messages.Add(message);
        while (Messages.Count > Keep)
        {
            Messages.RemoveAt(0);
        }

        if (Messages.Count == 1)
        {
            RaiseState();
        }
    }

    private void Remove(Func<ChatMessage, bool> match)
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (match(Messages[i]))
            {
                Messages.RemoveAt(i);
            }
        }
    }

    private void SetState(ChatState state)
    {
        if (state == _state)
        {
            return;
        }

        _state = state;
        RaiseState();
    }

    private void RaiseState() => RaiseMany(nameof(IsLive), nameof(StatusText), nameof(HasStatus));
}
