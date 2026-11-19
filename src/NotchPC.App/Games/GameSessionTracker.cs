using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Games;
using NotchPC.Platform.Shell;

namespace NotchPC.App.Games;

/// <summary>
/// Measures play sessions: the time a game spends in front and its FPS readings (from the overlay's meter). A game
/// that goes to the background is paused; its session ends when it closes, when another game comes to the front
/// or after a while in the background. Sessions of a couple of minutes or more go to the history.
/// </summary>
internal sealed class GameSessionTracker : IDisposable
{
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unity Player", "UnityPlayer", "Unreal Engine", "BootstrapPackagedGame", "Launcher", "Game",
    };

    private readonly GameHistoryStore _history;
    private readonly Func<string?, InstalledGame?> _gameForExe;
    private readonly DispatcherTimer _away;
    private Session? _current;

    public GameSessionTracker(GameHistoryStore history, Func<string?, InstalledGame?> gameForExe)
    {
        _history = history;
        _gameForExe = gameForExe;
        _away = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
        _away.Tick += (_, _) => CheckAway();
    }

    /// <summary>A session was saved (shown as a summary in the notch).</summary>
    public event EventHandler<GameSession>? Ended;

    /// <summary>A game is in front (called on every foreground change to it).</summary>
    public void InFront(uint pid, string processName)
    {
        if (_current is { } c && c.Pid != pid)
        {
            End();
        }

        if (_current is null)
        {
            var exe = AppActivator.ProcessPath(pid);
            _current = new Session(pid, NameFor(exe, processName), exe, DateTimeOffset.Now);
            AppLog.Info($"Game session started: {_current.Name}");
        }

        _current.FrontSince ??= DateTime.UtcNow;
        _current.AwaySince = null;
        _away.Stop();
    }

    /// <summary>One reading per second while the game is in front (null: no FPS meter).</summary>
    public void Sample(double? fps)
    {
        if (fps is { } f && _current?.FrontSince is not null)
        {
            _current.Fps.Add(f);
        }
    }

    /// <summary>The game left the front (alt-tab, desktop, closed).</summary>
    public void Away()
    {
        if (_current is not { FrontSince: { } since } c)
        {
            return;
        }

        c.Seconds += (DateTime.UtcNow - since).TotalSeconds;
        c.FrontSince = null;
        c.AwaySince = DateTime.UtcNow;
        _away.Start();
        CheckAway(); // closed already: end now
    }

    /// <summary>Ends and saves the running session (also on exit).</summary>
    public void End()
    {
        if (_current is not { } c)
        {
            return;
        }

        _current = null;
        _away.Stop();
        if (c.FrontSince is { } since)
        {
            c.Seconds += (DateTime.UtcNow - since).TotalSeconds;
        }

        var minutes = c.Seconds / 60;
        AppLog.Info($"Game session ended: {c.Name}, {minutes:0.0} min");
        if (minutes < GameSessions.MinMinutes)
        {
            return;
        }

        var session = new GameSession(c.Name, c.Start, Math.Round(minutes, 1), Round(c.Fps.Average), Round(c.Fps.Low), Round(c.Fps.Max), c.Exe);
        _history.Add(session);
        Ended?.Invoke(this, session);
    }

    public void Dispose() => End();

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;

    private static bool IsRunning(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // gone
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true; // protected process (anti-cheat): assume it still runs, the time limit ends it
        }
    }

    private void CheckAway()
    {
        if (_current is not { AwaySince: { } away } c)
        {
            _away.Stop();
            return;
        }

        if (DateTime.UtcNow - away > GameSessions.AwayLimit || !IsRunning(c.Pid))
        {
            End();
        }
    }

    /// <summary>The library's name for the game, else the exe's product name, else the process name.</summary>
    private string NameFor(string? exe, string processName)
    {
        if (_gameForExe(exe) is { } game)
        {
            return game.Name;
        }

        if (exe is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                foreach (var candidate in new[] { info.ProductName, info.FileDescription })
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && !GenericNames.Contains(candidate.Trim()))
                    {
                        return candidate.Trim();
                    }
                }
            }
            catch (FileNotFoundException)
            {
            }
        }

        return GameDetection.Clean(processName);
    }

    private sealed class Session(uint pid, string name, string? exe, DateTimeOffset start)
    {
        public uint Pid { get; } = pid;

        public string Name { get; } = name;

        public string? Exe { get; } = exe;

        public DateTimeOffset Start { get; } = start;

        public DateTime? FrontSince { get; set; }

        public DateTime? AwaySince { get; set; }

        public double Seconds { get; set; }

        public FpsSamples Fps { get; } = new();
    }
}
