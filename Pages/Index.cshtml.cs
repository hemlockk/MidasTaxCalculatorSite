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
            UserInput = new Stock
            {
                BuyDate = DateTime.Today.AddDays(-1)
            };
        }
        public void OnGet()
        {
            LoadStocksFromSession();
            LoadUserKeysFromSession();
            if (UserInput == null || UserInput.BuyDate == default)
            UserInput = new Stock { BuyDate = DateTime.Today.AddDays(-1) }; 
        }
        public bool HasUserEvdsKey => !string.IsNullOrWhiteSpace(UserEvdsKey);
        public bool HasUserFCSKey => !string.IsNullOrWhiteSpace(UserFCSKey);
        public class FcsLatestRoot
        {
            public bool status { get; set; }
            public List<FcsStockItem> response { get; set; } = [];
        }

        public class FcsStockItem
        {
            public string ticker { get; set; }
            public FcsActive active { get; set; }
        }

        public class FcsActive
        {
            public decimal? c { get; set; } // close / last price
        }
        public string? ErrorMessage { get; private set; }

        [BindProperty]
        public string? UserEvdsKey { get; set; }
        [BindProperty]
        public string? UserFCSKey { get; set; }
        public decimal TotalTax { get; set; }
        public decimal TotalProfit { get; set; }
        public bool TaxCalculated { get; set; }
        public DateTime FxDate { get; set; } = DateTime.Today;
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
            public decimal SellUfeIndex { get; set; }
            public decimal BuyRate { get; set; }
            public decimal SellRate { get; set; }
        }
        public bool HasResult { get; private set; }
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
        public async Task<decimal> GetCurrentPriceAsync(string stockCode)
        {
             var url =
                $"https://api-v4.fcsapi.com/stock/latest" +
                $"?symbol={stockCode}&access_key={GetFCSKey()}";

            using var client = new HttpClient();
            var httpResponse = await client.GetAsync(url);

            if (!httpResponse.IsSuccessStatusCode)
                throw new Exception($"FCS API error: {(int)httpResponse.StatusCode}");

            var json = await httpResponse.Content.ReadAsStringAsync();

            var data = JsonSerializer.Deserialize<FcsLatestRoot>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (data == null || !data.status)
                throw new Exception("FCS API returned invalid response.");

            // Priority: NYSE → NASDAQ
            var preferred = data.response
                .FirstOrDefault(x => x.ticker == $"NYSE:{stockCode}")
                ?? data.response.FirstOrDefault(x => x.ticker == $"NASDAQ:{stockCode}");

            if (preferred?.active?.c == null)
                throw new Exception("US stock price not found (NYSE/NASDAQ).");

            return preferred.active.c.Value;
        }
        public IActionResult OnPostAddStock()
        {
            LoadStocksFromSession();
            LoadUserKeysFromSession();
            var newStock = new Stock
            {
                StockCode = UserInput.StockCode.ToUpper(),
                BuyDate = UserInput.BuyDate,
                BuyAmount = UserInput.BuyAmount,
                BuyPrice = UserInput.BuyPrice
            };

            CreatedStocks.Add(newStock);
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
            foreach (var stock in stocks)
            {
                stock.BuyUfeIndex = GetUfeIndexForDate(ufeDict, stock.BuyDate.AddMonths(-1));
                stock.SellUfeIndex = GetUfeIndexForDate(ufeDict, DateTime.Today.AddMonths(-1));
                stock.CurrentPrice = await GetCurrentPriceAsync(stock.StockCode);
                stock.BuyRate = await GetUsdTryRateAsync(stock.BuyDate.AddDays(-1));
                stock.SellRate = currentRate;
                decimal inflationAdjustedBuyPrice = stock.BuyPrice;

                if (stock.SellUfeIndex / stock.BuyUfeIndex > 1.1m) // Inflation adjustment applies only if there is more than 10% increase
                {
                    inflationAdjustedBuyPrice *= stock.SellUfeIndex / stock.BuyUfeIndex;
                }
                decimal profit = (stock.CurrentPrice * stock.SellRate - inflationAdjustedBuyPrice * stock.BuyRate) * stock.BuyAmount;
                stock.Profit = profit > 0 ? profit : 0;
                stock.MinTaxRateApplied = Math.Round(stock.Profit * 0.15m, 2);
                income += profit;
            }
            TotalProfit = income; // Store total profit for display
            if (income <= 0) 
            {
                return 0;
            }
            else if (income <= 110000)
            {
                return income * 0.15m;
            }
            else if (income <= 230000)
            {
                return 16500m + (income - 110000m) * 0.20m;
            }
            else if (income <= 580000)
            {
                return 40500m + (income - 230000m) * 0.27m;
            }
            else if (income <= 3000000)
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
                UserInput = new Stock { BuyDate = DateTime.Today.AddDays(-1) };
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
        private decimal GetUfeIndexForDate(
            Dictionary<string, decimal> ufeDict,
            DateTime date)
        {
            string key = $"{date.Year}-{date.Month}";

            if (ufeDict.TryGetValue(key, out var value))
                return value;

            // Fallback: previous month (important!)
            var prev = date.AddMonths(-1);
            string prevKey = $"{prev.Year}-{prev.Month}";

            if (ufeDict.TryGetValue(prevKey, out var prevValue))
                return prevValue;

            throw new Exception($"ÜFE değeri bulunamadı: {key}");
        }
        public IActionResult OnPostClear()
        {
            LoadUserKeysFromSession();
            HttpContext.Session.Remove("Stocks");
            UserInput = new Stock { BuyDate = DateTime.Today.AddDays(-1) };
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
            UserInput = new Stock { BuyDate = DateTime.Today.AddDays(-1) };
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
            HttpContext.Session.SetString("UserFCSKey", UserFCSKey ?? "");
        }
        public void LoadUserKeysFromSession()
        {
            UserEvdsKey = HttpContext.Session.GetString("UserEvdsKey");
            UserFCSKey = HttpContext.Session.GetString("UserFCSKey");
        }
        private string GetEvdsKey()
        {
            return !string.IsNullOrWhiteSpace(UserEvdsKey)
                ? UserEvdsKey
                : _config["ApiKeys:Evds"];
        }
        private string GetFCSKey()
        {
            return !string.IsNullOrWhiteSpace(UserFCSKey)
                ? UserFCSKey
                : _config["ApiKeys:FCS"];
        }
        public IActionResult OnPostSaveKeys()
        {
            SaveUserKeysToSession();
            LoadUserKeysFromSession();
            LoadStocksFromSession();
            UserInput = new Stock { BuyDate = DateTime.Today.AddDays(-1) };
            return Page();
        }
    }
    
}