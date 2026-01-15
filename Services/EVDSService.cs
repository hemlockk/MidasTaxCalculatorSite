using System.Text.Json;
using System.Globalization;
using System.Net;
using MidasTaxCalculatorSite.Models;
namespace MidasTaxCalculatorSite.Services;


public class EVDSService
{
 public async Task<decimal> GetUsdTryRateAsync(DateTime date,string evdsKey)
{
    if (date > DateTime.Today.AddDays(-1))
    {
        date = DateTime.Today.AddDays(-1);
    }
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
public UfeResult GetUfeIndexForDate(
            Dictionary<string, decimal> ufeDict,
            DateTime date,
            string? WarningMessage)
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
public async Task<List<UfeItem>> GetUfeIndexValuesAsync(string evdsKey)
{
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
}