namespace MidasTaxCalculatorSite.Models;
using System.Text.Json.Serialization;
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

public class UfeResult
{
    public decimal Value { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string Key => $"{Year}-{Month}";
}

