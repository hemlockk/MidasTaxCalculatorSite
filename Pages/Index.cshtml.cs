using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using MidasTaxCalculatorSite.Models;
using MidasTaxCalculatorSite.Services;

namespace MidasTaxCalculatorSite.Pages
{
    public class IndexModel : PageModel
    {
        private readonly SessionService _sessionService;
        private readonly EVDSService _evdsService;
        private readonly StockService _stockService;
        private readonly IConfiguration _config;
        public IndexModel(IConfiguration config,
            SessionService sessionService,
            EVDSService evdsService,
            StockService stockService)
        {
            _sessionService = sessionService;
            _evdsService = evdsService;
            _stockService = stockService;   
            _config = config;
            UserInput = new Stock{};
        }
        public void OnGet()
        {
            if (UserInput == null || UserInput.BuyDate == default)
            UserInput = new Stock { BuyDate = DateTime.Today.AddDays(-1) }; 
        }
        public bool HasUserEvdsKey => !string.IsNullOrWhiteSpace(UserEvdsKey);
        public bool HasUserYahooKey => !string.IsNullOrWhiteSpace(UserYahooKey);
        public string? ErrorMessage { get; private set; }
        public string? WarningMessage { get; private set; }
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
        public bool HasResult { get; private set; }

        public IActionResult OnPostAddStock()
        {
            CreatedStocks = _sessionService.LoadStocks(HttpContext.Session);
            var keys = _sessionService.LoadUserKeys(HttpContext.Session);
            UserEvdsKey = keys.evdsKey;
            UserYahooKey = keys.yahooKey;
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
            _sessionService.SaveStocks(HttpContext.Session, CreatedStocks);
            return Page();
        }
        private async Task<decimal> CalculateTaxAsync(List<Stock> stocks)
        {
            var usdTryTask = _evdsService.GetUsdTryRateAsync(
                DateTime.Today.AddDays(-1),
                GetEvdsKey()
            );

            var ufeTask = _evdsService.GetUfeIndexValuesAsync(
                GetEvdsKey()
            );

            var pricesTask = _stockService.GetCurrentPricesAsync(
                stocks,
                GetYahooApiKey()
            );

            await Task.WhenAll(usdTryTask, ufeTask, pricesTask);

            decimal currentRate = usdTryTask.Result;
            List<UfeItem> Items = ufeTask.Result;

            var ufeDict = Items
            .Where(x => !string.IsNullOrEmpty(x.UFEValue))
            .ToDictionary(
                x => x.UFEdate,
                x => decimal.Parse(x.UFEValue, CultureInfo.InvariantCulture)
            );


            
            var stockTasks = stocks.Select(async stock =>
            {
                if (stock.CurrentPrice == -1)
                {
                    stock.Splits.Clear();
                    stock.Profit = 0;
                    stock.MinTaxRateApplied = 0;
                    return 0m; // profit contribution
                }

                // --- UFE (sync, dictionary lookup)
                var buyUfe = _evdsService.GetUfeIndexForDate(
                    ufeDict,
                    stock.BuyDate.AddMonths(-1),
                    WarningMessage
                );

                stock.BuyUfeIndex = buyUfe.Value;
                stock.BuyUfeDate = buyUfe.Key;

                var sellUfe = _evdsService.GetUfeIndexForDate(
                    ufeDict,
                    DateTime.Today.AddMonths(-1),
                    WarningMessage
                );

                stock.SellUfeIndex = sellUfe.Value;
                stock.SellUfeDate = sellUfe.Key;

                // --- EVDS (async, cached)
                stock.BuyRate = await _evdsService.GetUsdTryRateAsync(
                    stock.BuyDate.AddDays(-1),
                    GetEvdsKey()
                );

                stock.SellRate = currentRate;

                // --- Splits (async, cached)
                var splits = await _stockService.GetStockSplitsAsync(
                    stock.StockCode,
                    GetYahooApiKey()
                );

                stock.Splits.Clear();
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

                // --- Calculations (pure CPU)
                decimal splitAdjustedAmount = stock.BuyAmount * totalSplitFactor;
                decimal adjustedBuyPrice = stock.BuyPrice / totalSplitFactor;

                if (stock.SellUfeIndex / stock.BuyUfeIndex > 1.1m)
                {
                    adjustedBuyPrice *= stock.SellUfeIndex / stock.BuyUfeIndex;
                }

                decimal profit =
                    (stock.CurrentPrice * stock.SellRate -
                    adjustedBuyPrice * stock.BuyRate)
                    * splitAdjustedAmount;

                stock.Profit = profit > 0 ? profit : 0;
                stock.MinTaxRateApplied = Math.Round(stock.Profit * 0.15m, 2);

                return stock.Profit; // 👈 return contribution
            });
            decimal[] profits = await Task.WhenAll(stockTasks);
            decimal income = profits.Sum();
            TotalProfit = income;
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
            CreatedStocks = _sessionService.LoadStocks(HttpContext.Session);
            var keys = _sessionService.LoadUserKeys(HttpContext.Session);
            UserEvdsKey = keys.evdsKey;
            UserYahooKey = keys.yahooKey;
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
        public IActionResult OnPostClear()
        {
            var keys = _sessionService.LoadUserKeys(HttpContext.Session);
            UserEvdsKey = keys.evdsKey;
            UserYahooKey = keys.yahooKey;
            HttpContext.Session.Remove("Stocks");
            return Page();
        }
        public IActionResult OnPostDeleteStock(int index)
        {
            CreatedStocks = _sessionService.LoadStocks(HttpContext.Session);
            var keys = _sessionService.LoadUserKeys(HttpContext.Session);
            UserEvdsKey = keys.evdsKey;
            UserYahooKey = keys.yahooKey;
            if (index >= 0 && index < CreatedStocks.Count)
            {
                CreatedStocks.RemoveAt(index);
                _sessionService.SaveStocks(HttpContext.Session, CreatedStocks);
            }

            return Page();
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
            // 1️⃣ Save keys to session
            _sessionService.SaveUserKeys(
                HttpContext.Session,
                UserEvdsKey,
                UserYahooKey
            );

            // 2️⃣ Reload keys (to keep PageModel in sync)
            var keys = _sessionService.LoadUserKeys(HttpContext.Session);
            UserEvdsKey = keys.evdsKey;
            UserYahooKey = keys.yahooKey;

            // 3️⃣ Reload stocks from session
            CreatedStocks = _sessionService.LoadStocks(HttpContext.Session);

            return Page();
        }
    }
    
}