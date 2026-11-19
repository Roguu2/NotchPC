using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NotchPC.Core.Search;

/// <summary>"15 usd w pln", "100 zł na eur", "20 € to $", "50 gbp" (→ PLN).</summary>
public sealed record CurrencyQuery(double Amount, string From, string To);

/// <summary>Currency conversion with the National Bank of Poland's daily average rates (free, no key, no account).</summary>
public static partial class Currency
{
    public const string RatesUrl = "https://api.nbp.pl/api/exchangerates/tables/A/?format=json";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zł"] = "PLN", ["zl"] = "PLN", ["złotych"] = "PLN", ["zlotych"] = "PLN", ["złote"] = "PLN", ["pln"] = "PLN",
        ["$"] = "USD", ["usd"] = "USD", ["dolar"] = "USD", ["dolary"] = "USD", ["dolarów"] = "USD", ["dolarow"] = "USD",
        ["€"] = "EUR", ["eur"] = "EUR", ["euro"] = "EUR",
        ["£"] = "GBP", ["gbp"] = "GBP", ["funt"] = "GBP", ["funty"] = "GBP", ["funtów"] = "GBP",
        ["chf"] = "CHF", ["frank"] = "CHF", ["franki"] = "CHF",
        ["czk"] = "CZK", ["korona"] = "CZK", ["korony"] = "CZK",
        ["jpy"] = "JPY", ["jen"] = "JPY", ["jeny"] = "JPY", ["¥"] = "JPY",
        ["uah"] = "UAH", ["hrywna"] = "UAH", ["hrywny"] = "UAH",
    };

    public static bool TryParse(string? text, out CurrencyQuery query)
    {
        query = default!;
        var m = Query().Match((text ?? "").Trim());
        if (!m.Success)
        {
            return false;
        }

        var number = m.Groups["n"].Value.Replace(" ", "", StringComparison.Ordinal).Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || Code(m.Groups["from"].Value) is not { } from)
        {
            return false;
        }

        var to = m.Groups["to"].Success && m.Groups["to"].Value.Length > 0 ? Code(m.Groups["to"].Value) : (from == "PLN" ? "EUR" : "PLN");
        if (to is null || to == from)
        {
            return false;
        }

        query = new CurrencyQuery(amount, from, to);
        return true;
    }

    /// <summary>"usd", "$", "dolary" → "USD"; three letters of a currency in the table are accepted as well.</summary>
    public static string? Code(string word)
    {
        var w = word.Trim();
        if (Aliases.TryGetValue(w, out var code))
        {
            return code;
        }

        return w.Length == 3 && w.All(char.IsLetter) ? w.ToUpperInvariant() : null;
    }

    /// <summary>NBP's table A → PLN per unit of each currency (PLN = 1).</summary>
    public static IReadOnlyDictionary<string, double> ParseRates(string json)
    {
        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["PLN"] = 1 };
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var table in doc.RootElement.EnumerateArray())
            {
                if (!table.TryGetProperty("rates", out var list))
                {
                    continue;
                }

                foreach (var r in list.EnumerateArray())
                {
                    if (r.TryGetProperty("code", out var c) && r.TryGetProperty("mid", out var mid) && mid.TryGetDouble(out var v) && v > 0)
                    {
                        rates[c.GetString()!] = v;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }

        return rates;
    }

    /// <summary>Converted amount, or null when a currency is not in the table.</summary>
    public static double? Convert(CurrencyQuery q, IReadOnlyDictionary<string, double> plnPerUnit)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(plnPerUnit);
        return plnPerUnit.TryGetValue(q.From, out var from) && plnPerUnit.TryGetValue(q.To, out var to)
            ? q.Amount * from / to
            : null;
    }

    [GeneratedRegex(@"^(?<n>\d[\d ]*(?:[.,]\d+)?)\s*(?<from>[^\s\d]+)(?:\s+(?:w|na|to|in|=|->|→)\s+(?<to>[^\s\d]+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Query();
}
