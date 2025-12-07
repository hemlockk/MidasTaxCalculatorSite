using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using Newtonsoft.Json;
using System.Globalization;
using System.ComponentModel.DataAnnotations;
namespace MidasTaxCalculatorSite.Pages
{
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
        public bool HasUserAlphaKey => !string.IsNullOrWhiteSpace(UserAlphaKey);
        public bool HasUserYahooKey => !string.IsNullOrWhiteSpace(UserYahooKey);
        public string UserEvdsKey { get; set; }
        [BindProperty]
        public string UserAlphaKey { get; set; }
        [BindProperty]
        public string UserYahooKey { get; set; }
        public decimal TotalTax { get; set; }
        public bool TaxCalculated { get; set; }
        public DateTime FxDate { get; set; } = DateTime.Today;
        public decimal? FxRate { get; set; }
        public bool FxHasResult { get; set; }
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
        }
        public decimal CalculatedTax { get; private set; }
        public bool HasResult { get; private set; }
        public List<decimal> LastUfeValues { get; set; } = new();
        public async Task<Stock> GetCurrentPriceAsync(Stock stock)
        {
            // alphavantage API
            var client = new HttpClient();
            var url = "https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol=" + stock.StockCode + "&apikey=" + _config["ApiKeys:AlphaVantage"];
            var json = await client.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement;
            var priceString = root
                .GetProperty("Global Quote")
                .GetProperty("05. price")
                .GetString();

            stock.CurrentPrice = decimal.Parse(priceString);
            if(stock.CurrentPrice != null && stock.CurrentPrice != 0)
            {
                return stock;
            }
            // Yahoo API in case other doesn't work
            else if(stock.CurrentPrice == null || stock.CurrentPrice == 0)
            {
                string yahooKey = _config["ApiKeys:Yahoo"];
                client = new HttpClient();

                    client.DefaultRequestHeaders.Add("x-rapidapi-key", yahooKey);
                    client.DefaultRequestHeaders.Add("x-rapidapi-host", "apidojo-yahoo-finance-v1.p.rapidapi.com");

                url = $"https://apidojo-yahoo-finance-v1.p.rapidapi.com/market/v2/get-quotes?region=US&symbols={stock.StockCode}";

                json = await client.GetStringAsync(url);

                using var doc2 = JsonDocument.Parse(json);

                var price = doc2.RootElement
                    .GetProperty("quoteResponse")
                    .GetProperty("result")[0]
                    .GetProperty("regularMarketPrice")
                    .GetDecimal();

                stock.CurrentPrice = price;

                return stock;
            }
            else
            {
                throw new Exception("Could not retrieve stock price from either API.");
            }
           
        }
        public IActionResult OnPostAddStock()
        {
            LoadStocksFromSession();

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
            var ufeValues = await FetchUfeFromWebAsync();
            decimal income = 0;
            decimal currentRate = await GetLatestUsdTryRateAsync();
            foreach (var stock in stocks)
            {
                stock.CurrentPrice = (await GetCurrentPriceAsync(stock)).CurrentPrice;
                decimal buyRate = await GetUsdTryRateAsync(stock.BuyDate);
                decimal inflationAdjustedBuyPrice = stock.BuyPrice;
                
                int totalMonths = ((DateTime.Today.Year - stock.BuyDate.Year) * 12) + DateTime.Today.Month - stock.BuyDate.Month;

                for (int i = 0; i < totalMonths; i++)
                {
                    inflationAdjustedBuyPrice *= 1 + ufeValues[i] / 100;
                }
                decimal profit = (stock.CurrentPrice * currentRate - inflationAdjustedBuyPrice * buyRate) * stock.BuyAmount;
                stock.Profit = profit > 0 ? profit : 0;
                stock.MinTaxRateApplied = Math.Round(stock.Profit * 0.15m, 2);
                income += profit;
            }
            if (income <= 110000)
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
            // created stocks list must already be populated from your Add Stock function

            TotalTax = await CalculateTaxAsync(CreatedStocks);
            TaxCalculated = true;

            return Page();
        }
        private async Task<decimal> GetLatestUsdTryRateAsync()
        {
            DateTime date = DateTime.Today;
            string evdsKey = _config["ApiKeys:Evds"];
            for (int i = 0; i < 10; i++) // look back up to 10 days
            {
                string dateString = date.ToString("dd-MM-yyyy");
                string url =
                    $"https://evds2.tcmb.gov.tr/service/evds/series=TP.DK.USD.S.YTL" +
                    $"&startDate={dateString}&endDate={dateString}&type=json";

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("key", evdsKey);

                string json = await client.GetStringAsync(url);

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
        private async Task<decimal> GetUsdTryRateAsync(DateTime date)
        {
            string dateString = date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            string evdsKey = _config["ApiKeys:Evds"];
            if (date > DateTime.Today.AddDays(-1))
            {
                dateString = DateTime.Today.AddDays(-1).ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            }

            string url =
                $"https://evds2.tcmb.gov.tr/service/evds/series=TP.DK.USD.S.YTL" +
                $"&startDate={dateString}&endDate={dateString}&type=json";

            using var client = new HttpClient();

            // Must be added as HTTP header (just like in Postman)
            client.DefaultRequestHeaders.Add("key", evdsKey);

            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"TCMB returned error: {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);

            var items = doc.RootElement.GetProperty("items");

            if (items.GetArrayLength() == 0)
            {
                throw new Exception($"No FX data found for {dateString}");
            }

            // This matches your Postman response EXACTLY
            string rateStr = items[0].GetProperty("TP_DK_USD_S_YTL").GetString();

            return decimal.Parse(rateStr, CultureInfo.InvariantCulture);
        }
        public IActionResult OnPostClear()
        {
            HttpContext.Session.Remove("Stocks");
            UserInput = new Stock { BuyDate = DateTime.Today.AddDays(-1) };
            return Page();
        }
        private async Task<List<decimal>> FetchUfeFromWebAsync()
        {
            string url = "https://www.tcmb.gov.tr/wps/wcm/connect/TR/TCMB+TR/Main+Menu/Istatistikler/Enflasyon+Verileri/Uretici+Fiyatlari";

            using var client = new HttpClient();
            string html = await client.GetStringAsync(url);

            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            var ufeValues = new List<decimal>();

            // Find all rows in tables
            var rows = doc.DocumentNode.SelectNodes("//table//tr");

            if (rows == null)
                return ufeValues;

            foreach (var row in rows)
            {
                var cells = row.SelectNodes("td");
                if (cells == null || cells.Count < 5)
                    continue;

                // Month:
                string monthText = cells[0].InnerText.Trim();

                // UFE column (5th column)
                string ufeText = cells[4].InnerText.Trim()
                                .Replace(",", "."); // decimal fix for TR locale

                if (decimal.TryParse(ufeText, System.Globalization.NumberStyles.Any, 
                                    System.Globalization.CultureInfo.InvariantCulture, 
                                    out decimal ufeValue))
                {
                    ufeValues.Add(ufeValue);
                }
            }

            // Return last 6 months only (like your Python code)
            return ufeValues;
        }
        public void SaveStocksToSession()
        {
            var json = JsonConvert.SerializeObject(CreatedStocks);
            HttpContext.Session.SetString("Stocks", json);
        }
        public void LoadStocksFromSession()
        {
            var json = HttpContext.Session.GetString("Stocks");
            if (!string.IsNullOrEmpty(json))
                CreatedStocks = JsonConvert.DeserializeObject<List<Stock>>(json);
        }
        public IActionResult OnPostDeleteStock(int index)
        {
            LoadStocksFromSession();

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
            HttpContext.Session.SetString("UserAlphaKey", UserAlphaKey ?? "");
            HttpContext.Session.SetString("UserYahooKey", UserYahooKey ?? "");
        }

        public void LoadUserKeysFromSession()
        {
            UserEvdsKey = HttpContext.Session.GetString("UserEvdsKey");
            UserAlphaKey = HttpContext.Session.GetString("UserAlphaKey");
            UserYahooKey = HttpContext.Session.GetString("UserYahooKey");
        }
        private string GetEvdsKey()
        {
            return !string.IsNullOrWhiteSpace(UserEvdsKey)
                ? UserEvdsKey
                : _config["ApiKeys:Evds"];
        }

        private string GetAlphaKey()
        {
            return !string.IsNullOrWhiteSpace(UserAlphaKey)
                ? UserAlphaKey
                : _config["ApiKeys:AlphaVantage"];
        }

        private string GetYahooKey()
        {
            return !string.IsNullOrWhiteSpace(UserYahooKey)
                ? UserYahooKey
                : _config["ApiKeys:Yahoo"];
        }
        public IActionResult OnPostSaveKeys()
        {
            SaveUserKeysToSession();
            return Page();
        }
    }
    
}