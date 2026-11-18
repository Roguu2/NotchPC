using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotchPC.Core.Widgets;

public enum WeatherKind
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    FreezingRain,
    Snow,
    Showers,
    SnowShowers,
    Thunderstorm,
}

public sealed record WeatherPlace(string Name, string Country, string Region, double Latitude, double Longitude)
{
    public string Display => string.IsNullOrEmpty(Region) || Region == Name ? $"{Name}, {Country}" : $"{Name}, {Region}, {Country}";
}

public sealed record WeatherNow(double TemperatureC, WeatherKind Kind, bool IsDay, double MaxC, double MinC, DateTimeOffset FetchedAt)
{
    /// <summary>Next hours, starting with the current hour (local time of the place).</summary>
    public IReadOnlyList<HourForecast> Hours { get; init; } = [];

    /// <summary>Next days, starting with tomorrow.</summary>
    public IReadOnlyList<DayForecast> Days { get; init; } = [];
}

public sealed record HourForecast(DateTime Time, double TemperatureC, WeatherKind Kind, bool IsDay, int PrecipitationChance);

public sealed record DayForecast(DateOnly Date, double MaxC, double MinC, WeatherKind Kind, int PrecipitationChance);

public sealed class WeatherUnavailableException : Exception
{
    public WeatherUnavailableException()
    {
    }

    public WeatherUnavailableException(string message)
        : base(message)
    {
    }

    public WeatherUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Open-Meteo (free, no key, https://open-meteo.com): geocoding, current weather, hourly and daily forecast.</summary>
public sealed class OpenMeteoClient(HttpClient http)
{
    private static readonly Uri GeocodingBase = new("https://geocoding-api.open-meteo.com/v1/search");
    private static readonly Uri ForecastBase = new("https://api.open-meteo.com/v1/forecast");

    public async Task<IReadOnlyList<WeatherPlace>> SearchAsync(string city, string language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return [];
        }

        var url = new Uri($"{GeocodingBase}?name={Uri.EscapeDataString(city.Trim())}&count=5&language={Uri.EscapeDataString(language)}&format=json");
        var response = await GetAsync<GeocodingResponse>(url, ct).ConfigureAwait(false);
        return (response?.Results ?? [])
            .Select(r => new WeatherPlace(r.Name ?? "", r.Country ?? "", r.Admin1 ?? "", r.Latitude, r.Longitude))
            .ToList();
    }

    public async Task<WeatherNow> GetCurrentAsync(double latitude, double longitude, CancellationToken ct)
    {
        var lat = latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("0.####", CultureInfo.InvariantCulture);
        var url = new Uri($"{ForecastBase}?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,is_day" +
                          "&hourly=temperature_2m,weather_code,is_day,precipitation_probability" +
                          "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_days=8&timezone=auto");
        var r = await GetAsync<ForecastResponse>(url, ct).ConfigureAwait(false)
                ?? throw new WeatherUnavailableException("Empty forecast response");
        if (r.Current is not { } c || r.Daily?.Max is not [var max, ..] || r.Daily?.Min is not [var min, ..])
        {
            throw new WeatherUnavailableException("Forecast response is missing fields");
        }

        return new WeatherNow(c.Temperature, WeatherCodes.KindOf(c.WeatherCode), c.IsDay == 1, max, min, DateTimeOffset.UtcNow)
        {
            Hours = ParseHours(r.Hourly, c.Time),
            Days = ParseDays(r.Daily),
        };
    }

    internal static IReadOnlyList<HourForecast> ParseHours(HourlyBlock? h, string? currentTime)
    {
        if (h?.Time is not { } times || h.Temperature is not { } temps || h.Code is not { } codes)
        {
            return [];
        }

        // Times are local to the place ("2026-11-17T20:00"); start at the hour that contains "now".
        var now = ParseLocal(currentTime) ?? DateTime.MinValue;
        var from = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var list = new List<HourForecast>();
        for (var i = 0; i < times.Count && i < temps.Count && i < codes.Count && list.Count < 48; i++)
        {
            if (ParseLocal(times[i]) is not { } t || t < from)
            {
                continue;
            }

            var day = h.IsDay is { } d && i < d.Count ? d[i] == 1 : t.Hour is >= 7 and < 19;
            var pp = h.Precipitation is { } p && i < p.Count ? p[i] ?? 0 : 0;
            list.Add(new HourForecast(t, temps[i], WeatherCodes.KindOf(codes[i]), day, pp));
        }

        return list;
    }

    internal static IReadOnlyList<DayForecast> ParseDays(DailyBlock? d)
    {
        if (d?.Time is not { } times || d.Max is not { } max || d.Min is not { } min)
        {
            return [];
        }

        var list = new List<DayForecast>();
        for (var i = 1; i < times.Count && i < max.Count && i < min.Count; i++) // [0] is today
        {
            if (!DateOnly.TryParse(times[i], CultureInfo.InvariantCulture, out var date))
            {
                continue;
            }

            var code = d.Code is { } c && i < c.Count ? c[i] : 3;
            var pp = d.Precipitation is { } p && i < p.Count ? p[i] ?? 0 : 0;
            list.Add(new DayForecast(date, max[i], min[i], WeatherCodes.KindOf(code), pp));
        }

        return list;
    }

    private static DateTime? ParseLocal(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private async Task<T?> GetAsync<T>(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new WeatherUnavailableException($"Open-Meteo returned {(int)response.StatusCode}");
            }

            return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new WeatherUnavailableException("Open-Meteo is unreachable", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new WeatherUnavailableException("Open-Meteo timed out", ex);
        }
        catch (JsonException ex)
        {
            throw new WeatherUnavailableException("Open-Meteo sent an unexpected response", ex);
        }
    }

    private sealed record GeocodingResponse([property: JsonPropertyName("results")] List<GeocodingResult>? Results);

    private sealed record GeocodingResult(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("country")] string? Country,
        [property: JsonPropertyName("admin1")] string? Admin1,
        [property: JsonPropertyName("latitude")] double Latitude,
        [property: JsonPropertyName("longitude")] double Longitude);

    internal sealed record ForecastResponse(
        [property: JsonPropertyName("current")] CurrentBlock? Current,
        [property: JsonPropertyName("hourly")] HourlyBlock? Hourly,
        [property: JsonPropertyName("daily")] DailyBlock? Daily);

    internal sealed record CurrentBlock(
        [property: JsonPropertyName("time")] string? Time,
        [property: JsonPropertyName("temperature_2m")] double Temperature,
        [property: JsonPropertyName("weather_code")] int WeatherCode,
        [property: JsonPropertyName("is_day")] int IsDay);

    internal sealed record HourlyBlock(
        [property: JsonPropertyName("time")] List<string>? Time,
        [property: JsonPropertyName("temperature_2m")] List<double>? Temperature,
        [property: JsonPropertyName("weather_code")] List<int>? Code,
        [property: JsonPropertyName("is_day")] List<int>? IsDay,
        [property: JsonPropertyName("precipitation_probability")] List<int?>? Precipitation);

    internal sealed record DailyBlock(
        [property: JsonPropertyName("time")] List<string>? Time,
        [property: JsonPropertyName("weather_code")] List<int>? Code,
        [property: JsonPropertyName("temperature_2m_max")] List<double>? Max,
        [property: JsonPropertyName("temperature_2m_min")] List<double>? Min,
        [property: JsonPropertyName("precipitation_probability_max")] List<int?>? Precipitation);
}

/// <summary>WMO weather interpretation codes used by Open-Meteo.</summary>
public static class WeatherCodes
{
    public static WeatherKind KindOf(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        51 or 53 or 55 => WeatherKind.Drizzle,
        56 or 57 or 66 or 67 => WeatherKind.FreezingRain,
        61 or 63 or 65 => WeatherKind.Rain,
        71 or 73 or 75 or 77 => WeatherKind.Snow,
        80 or 81 or 82 => WeatherKind.Showers,
        85 or 86 => WeatherKind.SnowShowers,
        95 or 96 or 99 => WeatherKind.Thunderstorm,
        _ => WeatherKind.Cloudy,
    };
}
