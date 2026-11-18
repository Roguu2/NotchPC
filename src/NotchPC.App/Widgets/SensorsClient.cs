using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Widgets;
using NotchPC.Platform.Startup;

namespace NotchPC.App.Widgets;

/// <summary>
/// Runs the NotchPC.Sensors helper only while something on screen needs hardware stats (stats widget now,
/// game overlay later). The app is the named-pipe server; the helper connects, writes one JSON line per second
/// and exits when the pipe closes. With the optional elevated scheduled task the helper runs as administrator
/// (CPU temperature); otherwise it is started directly, without admin rights.
/// </summary>
internal sealed class SensorsClient : IDisposable
{
    private readonly HashSet<object> _consumers = [];
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _cts;
    private Process? _ownProcess;
    private Task _run = Task.CompletedTask;

    public SensorsClient()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public event EventHandler<SensorSnapshot>? SnapshotReceived;

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "sensors", "NotchPC.Sensors.exe");

    public bool IsRunning => _cts is not null;

    public void Acquire(object consumer)
    {
        if (_consumers.Add(consumer) && _consumers.Count == 1)
        {
            Start();
        }
    }

    public void Release(object consumer)
    {
        if (_consumers.Remove(consumer) && _consumers.Count == 0)
        {
            Stop();
        }
    }

    /// <summary>Restart (e.g. after the elevated task was created or removed) if currently running.</summary>
    public void Restart()
    {
        if (IsRunning)
        {
            Stop();
            Start();
        }
    }

    public void Dispose()
    {
        _consumers.Clear();
        Stop();
    }

    private void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _run = RunLoopAsync(_run, _cts.Token);
    }

    /// <summary>
    /// Keeps the helper connected while something needs it: waits for the previous run (and its helper process) to
    /// end first, then connects; if the helper does not connect or goes away, tries again with a growing pause.
    /// </summary>
    private async Task RunLoopAsync(Task previous, CancellationToken ct)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The previous run already logged its own errors.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            // The elevated scheduled task does not start a second helper while the old one still runs
            // (it exits by itself about a second after its pipe closed): wait for it, or the start is ignored.
            await WaitForOldHelpersAsync(ct).ConfigureAwait(false);
            var delivered = await RunAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            attempt = delivered ? 1 : attempt + 1;
            var pause = TimeSpan.FromSeconds(Math.Min(30, 2 * attempt));
            AppLog.Info($"Sensors helper stopped or did not connect; retrying in {pause.TotalSeconds:0} s");
            try
            {
                await Task.Delay(pause, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task WaitForOldHelpersAsync(CancellationToken ct)
    {
        for (var i = 0; i < 25; i++)
        {
            var running = Process.GetProcessesByName("NotchPC.Sensors");
            var any = running.Length > 0;
            foreach (var p in running)
            {
                p.Dispose();
            }

            if (!any)
            {
                return;
            }

            try
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        // The helper exits by itself on the next write to the closed pipe; make sure of it.
        var p = _ownProcess;
        _ownProcess = null;
        if (p is not null)
        {
            _ = Task.Delay(3000).ContinueWith(_ =>
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    p.Dispose();
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>One connection to the helper. True when it delivered readings (it ran, then stopped).</summary>
    private async Task<bool> RunAsync(CancellationToken ct)
    {
        var name = SensorProtocol.PipeName(Process.GetCurrentProcess().SessionId);
        var delivered = false;
        try
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var connect = pipe.WaitForConnectionAsync(ct);
            await Task.Run(LaunchHelper, ct);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await connect.WaitAsync(timeout.Token);
            }

            using var reader = new StreamReader(pipe);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break; // helper exited
                }

                if (SensorProtocol.Deserialize(line) is { } snapshot)
                {
                    delivered = true;
                    _ui.Post(_ => SnapshotReceived?.Invoke(this, snapshot), null);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped, or the helper never connected.
        }
        catch (IOException ex)
        {
            AppLog.Error("Sensors pipe failed", ex);
        }

        return delivered;
    }

    private void LaunchHelper()
    {
        // Elevated scheduled task first (CPU temperature); it needs no UAC prompt once created.
        if (ElevatedSensorsTask.Exists() && ElevatedSensorsTask.TryRun())
        {
            return;
        }

        if (!File.Exists(HelperPath))
        {
            AppLog.Error($"Sensors helper missing: {HelperPath}");
            return;
        }

        _ownProcess = Process.Start(new ProcessStartInfo(HelperPath)
        {
            ArgumentList = { "--pipe", SensorProtocol.PipeName(Process.GetCurrentProcess().SessionId), "--parent", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
