using System.Diagnostics;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using NotchPC.Core.Widgets;

namespace NotchPC.Sensors;

/// <summary>
/// Hardware sensor helper for NotchPC. Connects to the app's named pipe and writes one JSON line per second.
/// Exits as soon as the pipe breaks (the app closed it because nothing showing stats is on screen) or the
/// parent app exits. Started either directly by the app (no admin) or by the optional elevated scheduled task.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private static int Main(string[] args)
    {
        var pipeName = ArgValue(args, "--pipe") ?? SensorProtocol.PipeName(Process.GetCurrentProcess().SessionId);
        var parentPid = int.TryParse(ArgValue(args, "--parent"), out var p) ? p : (int?)null;

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
        try
        {
            pipe.Connect(5000);
        }
        catch (TimeoutException)
        {
            return 2; // the app is not listening
        }

        using var writer = new StreamWriter(pipe) { AutoFlush = true, NewLine = "\n" };
        using var reader = new HardwareReader();
        var elevated = IsElevated();

        while (true)
        {
            if (parentPid is { } pid && !ProcessAlive(pid))
            {
                return 0;
            }

            var started = Stopwatch.GetTimestamp();
            var snapshot = reader.Read() with { Elevated = elevated };
            try
            {
                writer.WriteLine(SensorProtocol.Serialize(snapshot));
            }
            catch (IOException)
            {
                return 0; // app closed the pipe
            }

            var spent = Stopwatch.GetElapsedTime(started);
            if (spent < Interval)
            {
                Thread.Sleep(Interval - spent);
            }
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>CPU/GPU via LibreHardwareMonitor, RAM via GlobalMemoryStatusEx, network via interface byte counters.</summary>
internal sealed class HardwareReader : IDisposable
{
    private readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    private long _lastRx;
    private long _lastTx;
    private long _lastNetTimestamp;

    public HardwareReader()
    {
        _computer.Open();
    }

    public SensorSnapshot Read()
    {
        double cpuLoad = 0;
        double? cpuTemp = null, gpuLoad = null, gpuTemp = null;
        string? cpuName = null, gpuName = null;

        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            if (hw.HardwareType == HardwareType.Cpu)
            {
                cpuName ??= hw.Name;
                cpuLoad = Value(hw, SensorType.Load, "CPU Total") ?? cpuLoad;
                cpuTemp ??= Value(hw, SensorType.Temperature, "CPU Package")
                            ?? Value(hw, SensorType.Temperature, "Core (Tctl/Tdie)")
                            ?? Value(hw, SensorType.Temperature, "Core Average")
                            ?? Max(hw, SensorType.Temperature);
            }
            else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                // Prefer a dedicated GPU over the integrated one.
                if (gpuName is not null && hw.HardwareType == HardwareType.GpuIntel)
                {
                    continue;
                }

                gpuName = hw.Name;
                gpuLoad = Value(hw, SensorType.Load, "GPU Core") ?? gpuLoad;
                gpuTemp = Value(hw, SensorType.Temperature, "GPU Core") ?? Max(hw, SensorType.Temperature) ?? gpuTemp;
            }
        }

        var (ramLoad, used, total) = Memory();
        var (down, up) = Network();
        return new SensorSnapshot
        {
            CpuLoad = Math.Round(cpuLoad, 1),
            CpuTemp = Round(cpuTemp),
            GpuLoad = Round(gpuLoad),
            GpuTemp = Round(gpuTemp),
            RamLoad = Math.Round(ramLoad, 1),
            RamUsedGb = Math.Round(used, 1),
            RamTotalGb = Math.Round(total, 1),
            NetDown = Math.Round(down),
            NetUp = Math.Round(up),
            CpuName = cpuName,
            GpuName = gpuName,
        };
    }

    public void Dispose() => _computer.Close();

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;

    private static double? Value(IHardware hw, SensorType type, string name)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == type && s.Name == name && s.Value is { } v && float.IsFinite(v) && v > 0)
            {
                return v;
            }
        }

        return null;
    }

    private static double? Max(IHardware hw, SensorType type)
    {
        double? max = null;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == type && s.Value is { } v && float.IsFinite(v) && v > 0 && (max is null || v > max))
            {
                max = v;
            }
        }

        return max;
    }

    private static (double Load, double UsedGb, double TotalGb) Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return (0, 0, 0);
        }

        const double gb = 1024d * 1024 * 1024;
        var total = status.TotalPhys / gb;
        var used = (status.TotalPhys - status.AvailPhys) / gb;
        return (status.MemoryLoad, used, total);
    }

    private (double Down, double Up) Network()
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var stats = nic.GetIPStatistics();
            rx += stats.BytesReceived;
            tx += stats.BytesSent;
        }

        var now = Stopwatch.GetTimestamp();
        double down = 0, up = 0;
        if (_lastNetTimestamp != 0)
        {
            var seconds = Stopwatch.GetElapsedTime(_lastNetTimestamp, now).TotalSeconds;
            if (seconds > 0)
            {
                down = Math.Max(0, rx - _lastRx) / seconds;
                up = Math.Max(0, tx - _lastTx) / seconds;
            }
        }

        _lastRx = rx;
        _lastTx = tx;
        _lastNetTimestamp = now;
        return (down, up);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
