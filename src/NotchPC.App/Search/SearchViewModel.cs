using System.Collections.ObjectModel;
using System.Net.Http;
using NotchPC.App.Infrastructure;
using NotchPC.Core.Localization;
using NotchPC.Core.Search;

namespace NotchPC.App.Search;

internal enum SearchKind
{
    Calculation,
    Currency,
}

/// <summary>One result row.</summary>
internal sealed class SearchResult : ObservableObject
{
    private bool _selected;

    public required SearchKind Kind { get; init; }

    public required string Title { get; init; }

    public string Subtitle { get; init; } = "";

    public string Glyph { get; init; } = "";

    /// <summary>The value copied with Enter.</summary>
    public string Value { get; init; } = "";

    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

/// <summary>
/// The calculator tab: arithmetic and currency conversion (NBP average rates) as you type. Enter copies the
/// selected result.
/// </summary>
internal sealed class SearchViewModel : ObservableObject
{
    private static readonly TimeSpan RatesFor = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly Action _done;
    private IReadOnlyDictionary<string, double>? _rates;
    private DateTime _ratesLoaded;
    private string _query = "";
    private int _selected;
    private int _version;

    public SearchViewModel(HttpClient http, Action done)
    {
        _http = http;
        _done = done;
    }

    public ObservableCollection<SearchResult> Results { get; } = [];

    public bool IsEmpty => _query.Trim().Length == 0;

    public string Query
    {
        get => _query;
        set
        {
            if (value == _query)
            {
                return;
            }

            _query = value ?? "";
            Raise(nameof(Query));
            Raise(nameof(IsEmpty));
            _ = UpdateAsync();
        }
    }

    public void Move(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        Select(Math.Clamp(_selected + delta, 0, Results.Count - 1));
    }

    /// <summary>Copies the result (Enter or a click) and closes the notch.</summary>
    public void Run(SearchResult? result = null)
    {
        result ??= _selected < Results.Count ? Results[_selected] : null;
        if (result is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(result.Value);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }

        Query = "";
        _done();
    }

    /// <summary>Esc on an empty box: close the notch.</summary>
    public void Close() => _done();

    private void Select(int index)
    {
        for (var i = 0; i < Results.Count; i++)
        {
            Results[i].IsSelected = i == index;
        }

        _selected = index;
    }

    private async Task UpdateAsync()
    {
        var version = ++_version;
        var q = _query.Trim();
        var list = new List<SearchResult>();
        if (q.Length > 0)
        {
            var culture = Loc.Culture;
            if (Calculator.TryEvaluate(q, out var value))
            {
                var text = Calculator.Format(value, culture);
                list.Add(new SearchResult { Kind = SearchKind.Calculation, Title = "= " + text, Subtitle = Loc.Get("Search.Copy"), Glyph = "", Value = text });
            }

            if (Currency.TryParse(q, out var cq))
            {
                var rates = await RatesAsync().ConfigureAwait(true);
                if (version != _version)
                {
                    return;
                }

                if (rates is not null && Currency.Convert(cq, rates) is { } converted)
                {
                    var text = converted.ToString("#,0.00", culture);
                    list.Add(new SearchResult
                    {
                        Kind = SearchKind.Currency,
                        Title = $"{cq.Amount.ToString("#,0.##", culture)} {cq.From} = {text} {cq.To}",
                        Subtitle = Loc.Get("Search.Currency.Source"),
                        Glyph = "",
                        Value = text,
                    });
                }
            }
        }

        if (version != _version)
        {
            return;
        }

        Results.Clear();
        foreach (var r in list)
        {
            Results.Add(r);
        }

        Select(0);
    }

    /// <summary>NBP's table A, cached for a few hours. Only the table is requested: nothing typed leaves the PC.</summary>
    private async Task<IReadOnlyDictionary<string, double>?> RatesAsync()
    {
        if (_rates is not null && DateTime.UtcNow - _ratesLoaded < RatesFor)
        {
            return _rates;
        }

        try
        {
            var json = await _http.GetStringAsync(Currency.RatesUrl).ConfigureAwait(true);
            _rates = Currency.ParseRates(json);
            _ratesLoaded = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AppLog.Info($"Currency rates unavailable: {ex.Message}");
        }

        return _rates;
    }
}
