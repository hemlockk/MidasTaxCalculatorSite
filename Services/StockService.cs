using System.Text.Json;
using MidasTaxCalculatorSite.Models;
namespace MidasTaxCalculatorSite.Services;

public class StockService
{
    public async Task GetCurrentPricesAsync(List<Stock> stocks, string YahooApiKey)
        {
            if (stocks == null || stocks.Count == 0)
                return;

            // Default everything to -1
            foreach (var stock in stocks)
            {
                stock.CurrentPrice = -1;
            }

            var symbols = string.Join(",", stocks.Select(s => s.StockCode));

            var url =
                $"https://apidojo-yahoo-finance-v1.p.rapidapi.com/market/v2/get-quotes" +
                $"?region=US&symbols={symbols}";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("x-rapidapi-host", "apidojo-yahoo-finance-v1.p.rapidapi.com");
            client.DefaultRequestHeaders.Add("x-rapidapi-key", YahooApiKey);

            using var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("quoteResponse", out var quoteResponse))
                return;

            if (!quoteResponse.TryGetProperty("result", out var resultElement))
                return;

            if (resultElement.ValueKind == JsonValueKind.Null ||
                resultElement.ValueKind == JsonValueKind.Undefined)
            {
                // All symbols invalid → keep -1
                return;
            }

            if (resultElement.ValueKind != JsonValueKind.Array)
                return;

            var lookup = stocks.ToDictionary(
                s => s.StockCode,
                s => s,
                StringComparer.OrdinalIgnoreCase
            );

            foreach (var item in resultElement.EnumerateArray())
            {
                if (!item.TryGetProperty("symbol", out var symbolProp))
                    continue;

                var symbol = symbolProp.GetString();
                if (symbol == null || !lookup.TryGetValue(symbol, out var stock))
                    continue;

                if (item.TryGetProperty("regularMarketPrice", out var priceProp))
                {
                    stock.CurrentPrice = priceProp.GetDecimal();
                }
            }
        }

    public async Task<List<StockSplit>> GetStockSplitsAsync(string stockCode, string YahooApiKey)
        {
            var url =
                $"https://apidojo-yahoo-finance-v1.p.rapidapi.com/stock/v2/get-chart" +
                $"?region=US&symbol={stockCode}&interval=1d&range=max&events=split";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("x-rapidapi-host", "apidojo-yahoo-finance-v1.p.rapidapi.com");
            client.DefaultRequestHeaders.Add("x-rapidapi-key", YahooApiKey);

            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return new List<StockSplit>();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("chart", out var chart) ||
                chart.ValueKind != JsonValueKind.Object)
                return new List<StockSplit>();

            if (!chart.TryGetProperty("result", out var resultArray) ||
                resultArray.ValueKind != JsonValueKind.Array ||
                resultArray.GetArrayLength() == 0)
                return new List<StockSplit>();

            var result = resultArray[0];

            if (!result.TryGetProperty("events", out var events) ||
                events.ValueKind != JsonValueKind.Object)
                return new List<StockSplit>();

            if (!events.TryGetProperty("splits", out var splitsElement) ||
                splitsElement.ValueKind != JsonValueKind.Object)
                return new List<StockSplit>();

            var splits = new List<StockSplit>();

            foreach (var splitProp in splitsElement.EnumerateObject())
            {
                var split = splitProp.Value;

                splits.Add(new StockSplit
                {
                    EffectiveDate = DateTimeOffset
                        .FromUnixTimeSeconds(split.GetProperty("date").GetInt64())
                        .UtcDateTime,

                    SplitFactor =
                        split.GetProperty("numerator").GetDecimal() /
                        split.GetProperty("denominator").GetDecimal()
                });
            }

            return splits
                .OrderBy(s => s.EffectiveDate)
                .ToList();
        }
}