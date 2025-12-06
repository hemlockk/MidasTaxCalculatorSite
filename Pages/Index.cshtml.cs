using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.FileSystemGlobbing.Internal.PathSegments;
namespace MidasTaxCalculatorSite.Pages
{
    public class IndexModel : PageModel
    {
        public IndexModel()
        {
            UserInput = new Stock
            {
                BuyDate = DateTime.Today.AddDays(-1)
            };
        }
        public decimal? TotalTax { get; set; }
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
            public string StockCode { get; set; }
            public DateTime BuyDate { get; set; }
            public decimal BuyAmount { get; set; }
            public decimal BuyPrice { get; set; }
            public decimal CurrentPrice { get; set; }
        }
        public decimal CalculatedTax { get; private set; }
        public bool HasResult { get; private set; }
        public List<decimal> LastUfeValues { get; set; } = new();
        public bool UfeTableLoaded { get; set; }
        public async Task<Stock> GetCurrentPriceAsync(Stock stock)
        {
            // alphavantage API
            var client = new HttpClient();
            var url = "https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol=" + stock.StockCode + "&apikey=***REMOVED***";
            var json = await client.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);

            var root = doc.RootElement;
            var priceString = root
                .GetProperty("Global Quote")
                .GetProperty("05. price")
                .GetString();

            stock.CurrentPrice = decimal.Parse(priceString);

            return stock;

            // Yahoo API
            /*
           var client = new HttpClient();

            client.DefaultRequestHeaders.Add("x-rapidapi-key", "***REMOVED***");
            client.DefaultRequestHeaders.Add("x-rapidapi-host", "apidojo-yahoo-finance-v1.p.rapidapi.com");

          var url = $"https://apidojo-yahoo-finance-v1.p.rapidapi.com/market/v2/get-quotes?region=US&symbols={stock.Symbol}";

          var json = await client.GetStringAsync(url);

          using var doc = JsonDocument.Parse(json);

           var price = doc.RootElement
            .GetProperty("quoteResponse")
            .GetProperty("result")[0]
            .GetProperty("regularMarketPrice")
            .GetDecimal();

          stock.CurrentPrice = price;

          return stock;*/
        }

        public void OnPost()
        {
            // Create new Stock from user input
            var newStock = new Stock
            {
                StockCode = UserInput.StockCode,
                BuyDate = UserInput.BuyDate,
                BuyAmount = UserInput.BuyAmount,
                BuyPrice = UserInput.BuyPrice,
                CurrentPrice = 0  // you can update later
            };

            CreatedStocks.Add(newStock);
        }

        private async Task<decimal> CalculateTaxAsync(List<Stock> stocks)
        {
            decimal income = 0;
            foreach (var stock in stocks)
            {
                stock.CurrentPrice = (await GetCurrentPriceAsync(stock)).CurrentPrice;
                decimal currentRate = await GetLatestUsdTryRateAsync();
                decimal buyRate = await GetUsdTryRateAsync(stock.BuyDate);
                decimal profit = (stock.CurrentPrice * currentRate - stock.BuyPrice * buyRate) * stock.BuyAmount;
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

            for (int i = 0; i < 10; i++) // look back up to 10 days
            {
                string dateString = date.ToString("dd-MM-yyyy");

                string url =
                    $"https://evds2.tcmb.gov.tr/service/evds/series=TP.DK.USD.S.YTL" +
                    $"&startDate={dateString}&endDate={dateString}&type=json";

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("key", "***REMOVED***");

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

            if (date > DateTime.Today.AddDays(-1))
            {
                dateString = DateTime.Today.AddDays(-1).ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            }

            string url =
                $"https://evds2.tcmb.gov.tr/service/evds/series=TP.DK.USD.S.YTL" +
                $"&startDate={dateString}&endDate={dateString}&type=json";

            using var client = new HttpClient();

            // Must be added as HTTP header (just like in Postman)
            client.DefaultRequestHeaders.Add("key", "***REMOVED***");

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
            CreatedStocks.Clear();
            return Page();
        }
        
        public async Task<IActionResult> OnPostFetchUfeAsync()
        {
            LastUfeValues = await FetchUfeFromWebAsync();
            UfeTableLoaded = true;
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
            return ufeValues.Take(30).ToList();
        }



    }
}