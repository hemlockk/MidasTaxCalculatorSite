using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using System.Globalization;
using System.Net;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace MidasTaxCalculatorSite.Pages
{
    public class ApiAuthorizationException : Exception
    {
        public ApiAuthorizationException(string message) : base(message)
        {
        }
    }
    public class IndexModel : PageModel
    {
        private readonly IConfiguration _config;
        public IndexModel(IConfiguration config)
        {
            _config = config;
            UserInput = new Stock{};
        }
        public void OnGet()
        {
            LoadStocksFromSession();
            LoadUserKeysFromSession();
            if (UserInput == null || UserInput.BuyDate == default)
            UserInput = new Stock { BuyDate = DateTime.Today.AddDays(-1) }; 
        }
        public bool HasUserEvdsKey => !string.IsNullOrWhiteSpace(UserEvdsKey);
        public bool HasUserYahooKey => !string.IsNullOrWhiteSpace(UserYahooKey);
        public string? ErrorMessage { get; private set; }
        public string? WarningMessage { get; private set; }
        public class UfeResult
        {
            public decimal Value { get; set; }
            public int Year { get; set; }
            public int Month { get; set; }
            public string Key => $"{Year}-{Month}";
        }
        [BindProperty]
        public string? UserEvdsKey { get; set; }
        [BindProperty]
        public string? UserYahooKey { get; set; }
        public decimal TotalTax { get; set; }
        public decimal TotalProfit { get; set; }
        public bool TaxCalculated { get; set; }
        [BindProperty]
        public Stock UserInput { get; set; } = new();
        [BindProperty]
        public List<Stock> CreatedStocks { get; set; } = new();
        public class Stock
        {
            [Required(ErrorMessage = "Lütfen hisse kodunu giriniz.")]
            [RegularExpression(@"^[A-Za-z\.]{1,5}$", ErrorMessage = "Hisse kodu yalnızca harf ve nokta içerebilir (en fazla 5 karakter).")]
            public string StockCode { get; set; }
            public DateTime BuyDate { get; set; }
            [Range(0.01, double.MaxValue, ErrorMessage = "Hisse miktarı 0'dan büyük olmalıdır.")]
            public decimal BuyAmount { get; set; }
            [Range(0.01, double.MaxValue, ErrorMessage = "Alım fiyatı 0'dan büyük olmalıdır.")]
            public decimal BuyPrice { get; set; }
            public decimal CurrentPrice { get; set; }
            public decimal Profit { get; set; }
            public decimal MinTaxRateApplied { get; set; }
            public decimal BuyUfeIndex { get; set; }
            public string BuyUfeDate { get; set; }
            public decimal SellUfeIndex { get; set; }
            public string SellUfeDate { get; set; }
            public decimal BuyRate { get; set; }
            public decimal SellRate { get; set; }
            public List<StockSplit> Splits { get; set; } = new();
        }
        public bool HasResult { get; private set; }
        public class StockSplit
        {
            public DateTime EffectiveDate { get; set; }
            public decimal SplitFactor { get; set; } // e.g. 10 for 10:1
        }

        public class UfeItem
        {
            [JsonPropertyName("Tarih")]
            public string UFEdate { get; set; }   // "2025-1"
            [JsonPropertyName("TP_TUFE1YI_T1")]
            public string UFEValue { get; set; }  // "3861.33"
        }
        public class UfeResponse
        {
            [JsonPropertyName("items")]
            public List<UfeItem> Items { get; set; }
        }
        public async Task GetCurrentPricesAsync(List<Stock> stocks)
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
            client.DefaultRequestHeaders.Add("x-rapidapi-key", GetYahooApiKey());

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
        public async Task<List<StockSplit>> GetStockSplitsAsync(string stockCode)
        {
            var url =
                $"https://apidojo-yahoo-finance-v1.p.rapidapi.com/stock/v2/get-chart" +
                $"?region=US&symbol={stockCode}&interval=1d&range=max&events=split";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("x-rapidapi-host", "apidojo-yahoo-finance-v1.p.rapidapi.com");
            client.DefaultRequestHeaders.Add("x-rapidapi-key", GetYahooApiKey());

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

        public IActionResult OnPostAddStock()
        {
            LoadStocksFromSession();
            LoadUserKeysFromSession();
            if(UserInput.BuyAmount > 0 && UserInput.BuyPrice > 0)
            {
                var newStock = new Stock
                {
                    StockCode = UserInput.StockCode.ToUpper(),
                    BuyDate = UserInput.BuyDate,
                    BuyAmount = UserInput.BuyAmount,
                    BuyPrice = UserInput.BuyPrice
                };
                CreatedStocks.Add(newStock);
            }
            SaveStocksToSession();
            return Page();
        }
        private async Task<decimal> CalculateTaxAsync(List<Stock> stocks)
        {
            decimal currentRate = await GetUsdTryRateAsync(DateTime.Today.AddDays(-1));
            List<UfeItem> Items = await GetUfeIndexValuesAsync();
            var ufeDict = Items
            .Where(x => !string.IsNullOrEmpty(x.UFEValue))
            .ToDictionary(
                x => x.UFEdate,
                x => decimal.Parse(x.UFEValue, CultureInfo.InvariantCulture)
            );
            decimal income = 0;
            GetCurrentPricesAsync(stocks);
            foreach (var stock in stocks)
            {
                var buyUfe = GetUfeIndexForDate(ufeDict, stock.BuyDate.AddMonths(-1));
                stock.BuyUfeIndex = buyUfe.Value;
                stock.BuyUfeDate = buyUfe.Key;
                var sellUfe = GetUfeIndexForDate(ufeDict, DateTime.Today.AddMonths(-1));
                stock.SellUfeIndex = sellUfe.Value;
                stock.SellUfeDate = sellUfe.Key;
                stock.BuyRate = await GetUsdTryRateAsync(stock.BuyDate.AddDays(-1));
                stock.SellRate = currentRate;
                var splits = await GetStockSplitsAsync(stock.StockCode); 
                decimal totalSplitFactor = 1m;               
                if (splits != null && splits.Count > 0)
                {
                    
                    foreach (var split in splits)
                    {
                        if (split.EffectiveDate >= stock.BuyDate)
                        {
                            totalSplitFactor *= split.SplitFactor;
                            stock.Splits.Add(split);
                        }
                    }
                
                }
                decimal SplitFactorAdjustedAmount = stock.BuyAmount * totalSplitFactor;
                decimal inflationAndSplitAdjustedBuyPrice = stock.BuyPrice / totalSplitFactor;
                if (stock.SellUfeIndex / stock.BuyUfeIndex > 1.1m) // Inflation adjustment applies only if there is more than 10% increase
                {
                    inflationAndSplitAdjustedBuyPrice *= stock.SellUfeIndex / stock.BuyUfeIndex;
                }
                decimal profit = (stock.CurrentPrice * stock.SellRate - inflationAndSplitAdjustedBuyPrice * stock.BuyRate) * SplitFactorAdjustedAmount;
                stock.Profit = profit > 0 ? profit : 0;
                stock.MinTaxRateApplied = Math.Round(stock.Profit * 0.15m, 2);
                income += stock.Profit;
            }
            TotalProfit = income; // Store total profit for display
            if (income <= 0) 
            {
                return 0;
            }
            else if (income <= 190000)
            {
                return income * 0.15m;
            }
            else if (income <= 400000)
            {
                return 16500m + (income - 110000m) * 0.20m;
            }
            else if (income <= 1500000)
            {
                return 40500m + (income - 230000m) * 0.27m;
            }
            else if (income <= 5300000)
            {
                return 135000m + (income - 580000m) * 0.35m;
            }
            else
            {
                return 982000m + (income - 3000000m) * 0.40m;
            }
        }
        public async Task<IActionResult> OnPostCalculateTaxAsync()
        {
            if (CreatedStocks.Count == 0)
            {
                ErrorMessage = "Eklenmiş hisse bulunmamaktadır.";
                return Page();
            }
                try
                {
                    TotalTax = await CalculateTaxAsync(CreatedStocks);
                    TaxCalculated = true;
                }
                catch (ApiAuthorizationException ex)
                {
                    ErrorMessage = ex.Message;
                }
                catch (Exception ex)
                {
                    ErrorMessage = "Beklenmeyen bir hata oluştu: " + ex.Message;
                }
                return Page();
        }
        private async Task<decimal> GetUsdTryRateAsync(DateTime date)
        {
            LoadUserKeysFromSession();
            if (date > DateTime.Today.AddDays(-1))
            {
                date = DateTime.Today.AddDays(-1);
            }
            string evdsKey = GetEvdsKey();
            for (int i = 0; i < 10; i++) // look back up to 10 days
            {
                string dateString = date.ToString("dd-MM-yyyy");
                string url =
                    $"https://evds2.tcmb.gov.tr/service/evds/series=TP.DK.USD.S.YTL" +
                    $"&startDate={dateString}&endDate={dateString}&type=json";

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("key", evdsKey);

                using var response = await client.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new ApiAuthorizationException(
                        "EVDS API anahtarı hatalı, süresi dolmuş veya kullanım limiti aşılmış olabilir."
                    );
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new ApiAuthorizationException(
                        "EVDS API anahtarı yetkisiz (401). Lütfen anahtarı kontrol ediniz."
                    );
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"EVDS API hatası: {(int)response.StatusCode}");
                }

                string json = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var items = doc.RootElement.GetProperty("items");


                if (items.GetArrayLength() > 0)
                {
                    var item = items[0];

                    if (item.TryGetProperty("TP_DK_USD_S_YTL", out var rateElement))
                    {
                        if (rateElement.ValueKind == JsonValueKind.String)
                        {
                            string rateStr = rateElement.GetString();

                            if (!string.IsNullOrWhiteSpace(rateStr))
                            {
                                return decimal.Parse(rateStr, CultureInfo.InvariantCulture);
                            }
                        }
                    }
                }

                // if we reach here → no valid rate → try previous day
                date = date.AddDays(-1);
            }

            throw new Exception("TCMB: No USD/TRY rate found in the last 10 days.");
        }
        private UfeResult GetUfeIndexForDate(
            Dictionary<string, decimal> ufeDict,
            DateTime date)
        {
            string key = $"{date.Year}-{date.Month}";

                if (ufeDict.TryGetValue(key, out var value))
                {
                    return new UfeResult
                    {
                        Value = value,
                        Year = date.Year,
                        Month = date.Month
                    };
                }
            
            // Fallback: previous month (important!) 
            var prev = date.AddMonths(-1); 
            string prevKey = $"{prev.Year}-{prev.Month}"; 
            if (ufeDict.TryGetValue(prevKey, out var prevValue))
            {
                WarningMessage = $"{key} Tarihi için ÜFE endeksi bulunamadı (Henüz açıklanmamış olabilir). {prevKey} endeksi kullanıldı.";
                return new UfeResult
                {
                    Value = prevValue,
                    Year = prev.Year,
                    Month = prev.Month
                };
            }
            throw new Exception($"ÜFE değeri bulunamadı: {key}");
        }
        public IActionResult OnPostClear()
        {
            LoadUserKeysFromSession();
            HttpContext.Session.Remove("Stocks");
            return Page();
        }
        private async Task<List<UfeItem>> GetUfeIndexValuesAsync()
        {
            LoadUserKeysFromSession();
            string evdsKey = GetEvdsKey();
            DateTime startDate = new DateTime(2014, 1, 1);
            DateTime endDate = DateTime.Today;
            string url =
                $"https://evds2.tcmb.gov.tr/service/evds/series=TP.TUFE1YI.T1" +
                $"&startDate={startDate:dd-MM-yyyy}" +
                $"&endDate={endDate:dd-MM-yyyy}" +
                $"&type=json";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("key", evdsKey);

            var json = await client.GetStringAsync(url);

            var result = JsonSerializer.Deserialize<UfeResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return result?.Items ?? new List<UfeItem>();
        }
        public void SaveStocksToSession()
        {
            var json = JsonSerializer.Serialize(CreatedStocks);
            HttpContext.Session.SetString("Stocks", json);
        }
        public void LoadStocksFromSession()
        {
            var json = HttpContext.Session.GetString("Stocks");
            if (!string.IsNullOrEmpty(json))
                CreatedStocks = JsonSerializer.Deserialize<List<Stock>>(json)
                ?? new List<Stock>();
        }
        public IActionResult OnPostDeleteStock(int index)
        {
            LoadStocksFromSession();
            LoadUserKeysFromSession();
            if (index >= 0 && index < CreatedStocks.Count)
            {
                CreatedStocks.RemoveAt(index);
                SaveStocksToSession();
            }

            return Page();
        }
        public void SaveUserKeysToSession()
        {
            HttpContext.Session.SetString("UserEvdsKey", UserEvdsKey ?? "");
            HttpContext.Session.SetString("UserYahooKey", UserYahooKey ?? "");
        }
        public void LoadUserKeysFromSession()
        {
            UserEvdsKey = HttpContext.Session.GetString("UserEvdsKey");
            UserYahooKey = HttpContext.Session.GetString("UserYahooKey");
        }
        private string GetEvdsKey()
        {
            return !string.IsNullOrWhiteSpace(UserEvdsKey)
                ? UserEvdsKey
                : _config["ApiKeys:Evds"];
        }
        private string GetYahooApiKey()
        {
            return !string.IsNullOrWhiteSpace(UserYahooKey)
                ? UserYahooKey
                : _config["ApiKeys:Yahoo"];
        }
        public IActionResult OnPostSaveKeys()
        {
            SaveUserKeysToSession();
            LoadUserKeysFromSession();
            LoadStocksFromSession();
            return Page();
        }
    }
    
}