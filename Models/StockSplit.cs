namespace MidasTaxCalculatorSite.Models;
public class StockSplit
{
    public DateTime EffectiveDate { get; set; }
    public decimal SplitFactor { get; set; } // e.g. 10 for 10:1
}
