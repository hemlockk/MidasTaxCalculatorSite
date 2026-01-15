using System.ComponentModel.DataAnnotations;
namespace MidasTaxCalculatorSite.Models;
public class ApiAuthorizationException : Exception
{
    public ApiAuthorizationException(string message) : base(message)
    {
    }
}

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