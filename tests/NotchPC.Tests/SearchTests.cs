using System.Globalization;
using NotchPC.Core.Search;

namespace NotchPC.Tests;

public sealed class SearchTests
{
    [Theory]
    [InlineData("2*19,99", 39.98)]
    [InlineData("2 * 19.99", 39.98)]
    [InlineData("= (1+2)*3", 9)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("2^10", 1024)]
    [InlineData("-3 + 5", 2)]
    [InlineData("200 * 15%", 30)]
    [InlineData("3 x 4", 12)]
    [InlineData("1 000 000 / 4", 250000)]
    [InlineData("sqrt(16)+1", 5)]
    [InlineData("2*pi", 6.283185307179586)]
    public void Calculations(string text, double expected)
    {
        Assert.True(Calculator.TryEvaluate(text, out var v));
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("42")]
    [InlineData("1/0")]
    [InlineData("(2+3")]
    [InlineData("2 + abc")]
    [InlineData("")]
    public void Not_a_calculation(string text) => Assert.False(Calculator.TryEvaluate(text, out _));

    [Fact]
    public void Result_text()
    {
        var pl = CultureInfo.GetCultureInfo("pl-PL");
        Assert.Equal("39,98", Calculator.Format(39.98000000000001, pl));
        Assert.Equal("1 234 567,5", Calculator.Format(1234567.5, pl).Replace(' ', ' '));
    }

    [Theory]
    [InlineData("15 usd w pln", 15, "USD", "PLN")]
    [InlineData("100 zł na eur", 100, "PLN", "EUR")]
    [InlineData("20 € to $", 20, "EUR", "USD")]
    [InlineData("50 gbp", 50, "GBP", "PLN")]
    [InlineData("10 pln", 10, "PLN", "EUR")]
    [InlineData("2,5 euro w funtach", 2.5, "EUR", null)]
    public void Currency_queries(string text, double amount, string from, string? to)
    {
        var ok = Currency.TryParse(text, out var q);
        if (to is null)
        {
            Assert.False(ok); // "funtach" is not a known word
            return;
        }

        Assert.True(ok);
        Assert.Equal(new CurrencyQuery(amount, from, to), q);
    }

    [Theory]
    [InlineData("spotify")]
    [InlineData("2+2")]
    [InlineData("15 usd w usd")]
    public void Not_a_currency_query(string text) => Assert.False(Currency.TryParse(text, out _));

    [Fact]
    public void Conversion_through_pln()
    {
        var rates = Currency.ParseRates("""[{"table":"A","rates":[{"code":"USD","mid":4.0},{"code":"EUR","mid":4.25}]}]""");
        Assert.Equal(60, Currency.Convert(new CurrencyQuery(15, "USD", "PLN"), rates)!.Value, 6);
        Assert.Equal(4.0 / 4.25 * 10, Currency.Convert(new CurrencyQuery(10, "USD", "EUR"), rates)!.Value, 6);
        Assert.Null(Currency.Convert(new CurrencyQuery(1, "XYZ", "PLN"), rates));
    }

}
