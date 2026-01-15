using System.Text.Json;
using MidasTaxCalculatorSite.Models;
using Microsoft.Extensions.Caching.Memory;
namespace MidasTaxCalculatorSite.Services;

public class StockService
{
    private readonly IMemoryCache _cache;
    private readonly HttpClient _httpClient;
    public StockService(HttpClient httpClient, IMemoryCache cache)
    {
        _cache = cache;
        _httpClient = httpClient;
    }
    public async Task GetCurrentPricesAsync(List<Stock> stocks, string yahooApiKey)
    {
        if (stocks == null || stocks.Count == 0)
            return;

        // Default everything
        foreach (var stock in stocks)
            stock.CurrentPrice = -1;

        var stocksToFetch = new List<Stock>();

        // 1️⃣ Try cache first
        foreach (var stock in stocks)
        {
            if (_cache.TryGetValue($"PRICE_{stock.StockCode}", out decimal cachedPrice))
            {
                stock.CurrentPrice = cachedPrice;
            }
            else
            {
                stocksToFetch.Add(stock);
            }
        }

        // Nothing to fetch
        if (stocksToFetch.Count == 0)
            return;

        // 2️⃣ Fetch only missing ones
        var symbols = string.Join(",", stocksToFetch.Select(s => s.StockCode));

        var url =
            $"https://apidojo-yahoo-finance-v1.p.rapidapi.com/market/v2/get-quotes" +
            $"?region=US&symbols={symbols}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-rapidapi-host", "apidojo-yahoo-finance-v1.p.rapidapi.com");
        request.Headers.Add("x-rapidapi-key", yahooApiKey);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Yahoo API hatası: {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        if (!doc.RootElement.TryGetProperty("quoteResponse", out var qr) ||
            !qr.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array)
            return;

        var lookup = stocksToFetch.ToDictionary(s => s.StockCode, StringComparer.OrdinalIgnoreCase);

        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("symbol", out var symProp) ||
                !item.TryGetProperty("regularMarketPrice", out var priceProp))
                continue;

            var symbol = symProp.GetString();
            if (symbol == null || !lookup.TryGetValue(symbol, out var stock))
                continue;

            var price = priceProp.GetDecimal();

            stock.CurrentPrice = price;

            // 🔐 Cache for 10 minutes
            _cache.Set(
                $"PRICE_{symbol}",
                price,
                TimeSpan.FromMinutes(10)
            );
        }
    }


    public async Task<List<StockSplit>> GetStockSplitsAsync(string stockCode, string yahooApiKey)
    {
        var cacheKey = $"SPLITS_{stockCode}";

        // 1️⃣ Cache first
        if (_cache.TryGetValue(cacheKey, out List<StockSplit> cachedSplits))
            return cachedSplits;

        var url =
            $"https://apidojo-yahoo-finance-v1.p.rapidapi.com/stock/v2/get-chart" +
            $"?region=US&symbol={stockCode}&interval=1d&range=max&events=split";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-rapidapi-host", "apidojo-yahoo-finance-v1.p.rapidapi.com");
        request.Headers.Add("x-rapidapi-key", yahooApiKey);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _cache.Set(cacheKey, new List<StockSplit>());
            return new List<StockSplit>();
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        if (!doc.RootElement.TryGetProperty("chart", out var chart) ||
            !chart.TryGetProperty("result", out var resultArray) ||
            resultArray.ValueKind != JsonValueKind.Array ||
            resultArray.GetArrayLength() == 0)
        {
            _cache.Set(cacheKey, new List<StockSplit>());
            return new List<StockSplit>();
        }

        var result = resultArray[0];

        if (!result.TryGetProperty("events", out var events) ||
            !events.TryGetProperty("splits", out var splitsElement) ||
            splitsElement.ValueKind != JsonValueKind.Object)
        {
            _cache.Set(cacheKey, new List<StockSplit>());
            return new List<StockSplit>();
        }

        var splits = new List<StockSplit>();

        foreach (var prop in splitsElement.EnumerateObject())
        {
            var split = prop.Value;

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

        splits = splits
            .OrderBy(s => s.EffectiveDate)
            .ToList();

        // 2️⃣ Cache FOREVER
        _cache.Set(cacheKey, splits);

        return splits;
    }

}