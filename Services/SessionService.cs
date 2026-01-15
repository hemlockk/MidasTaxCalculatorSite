using System.Text.Json;
using MidasTaxCalculatorSite.Models;
namespace MidasTaxCalculatorSite.Services;
public class SessionService
{
    private const string StocksKey = "Stocks";
    private const string UserEvdsKeyKey = "UserEvdsKey";
    private const string UserYahooKeyKey = "UserYahooKey";

    public void SaveStocks(ISession session, List<Stock> stocks)
    {
        var json = JsonSerializer.Serialize(stocks ?? new List<Stock>());
        session.SetString(StocksKey, json);
    }

    public List<Stock> LoadStocks(ISession session)
    {
        var json = session.GetString(StocksKey);
        if (string.IsNullOrWhiteSpace(json))
            return new List<Stock>();

        return JsonSerializer.Deserialize<List<Stock>>(json) ?? new List<Stock>();
    }

    public void SaveUserKeys(ISession session, string? evdsKey, string? yahooKey)
    {
        session.SetString(UserEvdsKeyKey, evdsKey ?? "");
        session.SetString(UserYahooKeyKey, yahooKey ?? "");
    }

    public (string? evdsKey, string? yahooKey) LoadUserKeys(ISession session)
    {
        var evdsKey = session.GetString(UserEvdsKeyKey);
        var yahooKey = session.GetString(UserYahooKeyKey);
        return (evdsKey, yahooKey);
    }
}