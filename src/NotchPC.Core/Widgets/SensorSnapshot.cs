using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotchPC.Core.Widgets;

/// <summary>
/// One reading from the NotchPC.Sensors process, sent once per second as a JSON line over a named pipe.
/// Null = the value is not available (e.g. CPU temperature without the PawnIO driver / administrator rights).
/// </summary>
public sealed record SensorSnapshot
{
    public double CpuLoad { get; init; }

    public double? CpuTemp { get; init; }

    public double? GpuLoad { get; init; }

    public double? GpuTemp { get; init; }

    public double RamLoad { get; init; }

    public double RamUsedGb { get; init; }

    public double RamTotalGb { get; init; }

    /// <summary>Bytes per second, all physical network adapters together.</summary>
    public double NetDown { get; init; }

    public double NetUp { get; init; }

    /// <summary>True when the sensors process runs with administrator rights.</summary>
    public bool Elevated { get; init; }

    public string? CpuName { get; init; }

    public string? GpuName { get; init; }
}

public static class SensorProtocol
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Pipe name for the current Windows session (an elevated helper in the same session finds it).</summary>
    public static string PipeName(int sessionId) => $"NotchPC.Sensors.{sessionId}";

    public static string Serialize(SensorSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    public static SensorSnapshot? Deserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<SensorSnapshot>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"2.3 MB/s", "176 KB/s", "0 B/s" (decimal units, like Task Manager's network graph).</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        var c = CultureInfo.InvariantCulture;
        return bytesPerSecond switch
        {
            >= 1_000_000_000 => (bytesPerSecond / 1_000_000_000).ToString("0.0", c) + " GB/s",
            >= 1_000_000 => (bytesPerSecond / 1_000_000).ToString("0.0", c) + " MB/s",
            >= 1_000 => (bytesPerSecond / 1_000).ToString("0", c) + " KB/s",
            _ => Math.Max(0, bytesPerSecond).ToString("0", c) + " B/s",
        };
    }
}
