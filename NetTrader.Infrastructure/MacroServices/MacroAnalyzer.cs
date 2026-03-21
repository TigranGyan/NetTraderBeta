using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetTrader.Infrastructure.MacroServices;

public class MacroAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MacroAnalyzer> _logger;

    private static readonly ConcurrentDictionary<string, (DateTime CachedAt, object Data)> MacroCache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public MacroAnalyzer(HttpClient httpClient, ILogger<MacroAnalyzer> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<int?> GetFearGreedIndexAsync()
    {
        const string cacheKey = "fear_greed_index";

        if (MacroCache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.CachedAt < CacheDuration)
        {
            _logger.LogInformation("📊 Fear & Greed Index из кэша: {Value}", cached.Data);
            return (int?)cached.Data;
        }

        try
        {
            var response = await _httpClient.GetAsync("https://api.alternative.me/fng/?limit=1&date_format=us");
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");

            if (data.GetArrayLength() > 0)
            {
                var firstEntry = data[0];
                int fgi = int.Parse(firstEntry.GetProperty("value").GetString() ?? "50");
                string classification = firstEntry.GetProperty("value_classification").GetString() ?? "Neutral";

                _logger.LogInformation("📊 Fear & Greed Index: {FGI}/100 ({Classification})", fgi, classification);

                MacroCache[cacheKey] = (DateTime.UtcNow, fgi);
                return fgi;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Не удалось получить Fear & Greed Index: {Error}", ex.Message);
        }

        return null;
    }

    /// <summary>
    /// FIX #12: Добавлено предупреждение когда все FOMC-даты в прошлом.
    /// Ранее метод тихо возвращал null, и бот просто не знал о FOMC.
    /// Теперь: логирует WARNING чтобы разработчик обновил даты на следующий год.
    /// </summary>
    public async Task<DateTime?> GetNextFOMCDateAsync()
    {
        const string cacheKey = "fomc_date";

        if (MacroCache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.CachedAt < TimeSpan.FromDays(1))
        {
            return (DateTime?)cached.Data;
        }

        try
        {
            // Даты FOMC — обновлять ежегодно!
            // Источник: https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm
            var fomcDates = new[]
            {
                // 2026
                new DateTime(2026, 1, 27),
                new DateTime(2026, 3, 17),
                new DateTime(2026, 5, 5),
                new DateTime(2026, 6, 16),
                new DateTime(2026, 7, 28),
                new DateTime(2026, 9, 15),
                new DateTime(2026, 11, 2),
                new DateTime(2026, 12, 15),
                // 2027 — добавить при появлении расписания
            };

            var today = DateTime.UtcNow.Date;
            var nextFOMC = fomcDates.FirstOrDefault(d => d.Date >= today);

            if (nextFOMC != default)
            {
                _logger.LogInformation("📅 Ближайшее FOMC: {Date:yyyy-MM-dd} (в {Days} дней)",
                    nextFOMC.Date, (nextFOMC.Date - today).Days);

                MacroCache[cacheKey] = (DateTime.UtcNow, nextFOMC.Date);
                return nextFOMC.Date;
            }

            // FIX #12: Все даты в прошлом — нужно обновить!
            _logger.LogWarning("⚠️ ВСЕ FOMC-даты в прошлом! Последняя: {Last:yyyy-MM-dd}. " +
                "Обновите массив fomcDates в MacroAnalyzer.GetNextFOMCDateAsync()!",
                fomcDates.Last());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Не удалось получить FOMC дату: {Error}", ex.Message);
        }

        return null;
    }

    public async Task<decimal?> GetBTCDominanceAsync()
    {
        const string cacheKey = "btc_dominance";

        if (MacroCache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.CachedAt < CacheDuration)
        {
            _logger.LogInformation("📊 BTC Dominance из кэша: {Value}%", cached.Data);
            return (decimal?)cached.Data;
        }

        // ═══ Попытка 1: CoinCap API v2 ═══
        try
        {
            var response = await _httpClient.GetAsync("https://api.coincap.io/v2/assets/bitcoin");

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("marketCapDominance", out var domProp))
                    {
                        if (decimal.TryParse(domProp.GetString(), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out decimal dom))
                        {
                            _logger.LogInformation("📊 BTC Dominance (CoinCap): {BTC:F2}%", dom);
                            MacroCache[cacheKey] = (DateTime.UtcNow, dom);
                            return dom;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug("CoinCap BTC.D: {Error}", ex.Message); }

        // ═══ Попытка 2: CoinGecko ═══
        try
        {
            var response = await _httpClient.GetAsync("https://api.coingecko.com/api/v3/global");

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var btcDominance = doc.RootElement
                    .GetProperty("data")
                    .GetProperty("market_cap_percentage")
                    .GetProperty("btc")
                    .GetDecimal();

                _logger.LogInformation("📊 BTC Dominance (CoinGecko): {BTC:F2}%", btcDominance);
                MacroCache[cacheKey] = (DateTime.UtcNow, btcDominance);
                return btcDominance;
            }
            else _logger.LogDebug("CoinGecko вернул {Status} — пропускаем", response.StatusCode);
        }
        catch (Exception ex) { _logger.LogDebug("CoinGecko BTC.D: {Error}", ex.Message); }

        // ═══ Попытка 3: CoinCap calc ═══
        try
        {
            var btcResponse = await _httpClient.GetAsync("https://api.coincap.io/v2/assets?limit=1&search=bitcoin");
            if (btcResponse.IsSuccessStatusCode)
            {
                using var btcDoc = JsonDocument.Parse(await btcResponse.Content.ReadAsStringAsync());
                var btcData = btcDoc.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();

                if (btcData.ValueKind != JsonValueKind.Undefined &&
                    btcData.TryGetProperty("marketCapUsd", out var mcProp))
                {
                    if (decimal.TryParse(mcProp.GetString(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out decimal btcMc))
                    {
                        var globalResponse = await _httpClient.GetAsync("https://api.coincap.io/v2/assets?limit=200");
                        if (globalResponse.IsSuccessStatusCode)
                        {
                            using var globalDoc = JsonDocument.Parse(await globalResponse.Content.ReadAsStringAsync());
                            decimal totalMc = 0;
                            foreach (var asset in globalDoc.RootElement.GetProperty("data").EnumerateArray())
                            {
                                if (asset.TryGetProperty("marketCapUsd", out var assetMc) &&
                                    decimal.TryParse(assetMc.GetString(), NumberStyles.Any,
                                        CultureInfo.InvariantCulture, out decimal mc))
                                    totalMc += mc;
                            }

                            if (totalMc > 0)
                            {
                                decimal dominance = btcMc / totalMc * 100m;
                                _logger.LogInformation("📊 BTC Dominance (CoinCap calc): {BTC:F2}%", dominance);
                                MacroCache[cacheKey] = (DateTime.UtcNow, dominance);
                                return dominance;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug("CoinCap calc BTC.D: {Error}", ex.Message); }

        _logger.LogWarning("⚠️ Не удалось получить BTC Dominance из всех источников. Используется fallback.");
        return null;
    }

    public async Task<Dictionary<string, object>> GetAllMacroDataAsync()
    {
        var macroData = new Dictionary<string, object>();

        try
        {
            var fgiTask = GetFearGreedIndexAsync();
            var fomcTask = GetNextFOMCDateAsync();
            var btcdTask = GetBTCDominanceAsync();

            await Task.WhenAll(fgiTask, fomcTask, btcdTask);

            var fgi = await fgiTask;
            var fomc = await fomcTask;
            var btcd = await btcdTask;

            if (fgi.HasValue) macroData["FearGreedIndex"] = fgi.Value;
            if (fomc.HasValue) macroData["FOMCDate"] = fomc.Value;
            if (btcd.HasValue) macroData["BTCDominance"] = btcd.Value;

            _logger.LogInformation("✅ Макро: FGI={FGI}, FOMC={FOMC}, BTC.D={BTCD}%",
                fgi, fomc?.ToString("yyyy-MM-dd") ?? "Unknown", btcd);

            return macroData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка макро-данных");
            return macroData;
        }
    }

    public void ClearMacroCache()
    {
        MacroCache.Clear();
        _logger.LogInformation("🗑️ Кэш макро-данных очищен");
    }

    public async Task<string> GetMacroStatusAsync()
    {
        var macroData = await GetAllMacroDataAsync();
        var status = new System.Text.StringBuilder();

        status.AppendLine("╔════════════════════════════════════════════════════════╗");
        status.AppendLine("║             СТАТУС МАКРО-УСЛОВИЙ                      ║");
        status.AppendLine("╠════════════════════════════════════════════════════════╣");

        if (macroData.TryGetValue("FearGreedIndex", out var fgiObj) && fgiObj is int fgi)
        {
            string fgiStatus = fgi switch
            {
                > 75 => "🔴 ЭКСТРЕМАЛЬНАЯ ЖАДНОСТЬ - Short на альты",
                > 60 => "🟠 ЖАДНОСТЬ - Осторожнее с Long",
                >= 40 and <= 60 => "🟡 НЕЙТРАЛЬ - Все направления ОК",
                > 25 => "🟢 СТРАХ - Агрессивные Long",
                _ => "🔵 ЭКСТРЕМАЛЬНЫЙ СТРАХ - Максимум инвестиций"
            };
            status.AppendLine($"║ Fear & Greed: {fgi:D3}/100  {fgiStatus,-38}║");
        }

        if (macroData.TryGetValue("FOMCDate", out var fomcObj) && fomcObj is DateTime fomc)
        {
            TimeSpan daysUntil = fomc.Date - DateTime.UtcNow.Date;
            string fomcStatus = daysUntil.TotalDays switch
            {
                >= 0 and <= 3 => "⚠️ БЛИЗКО: Расширь сетки",
                > 3 and <= 14 => "⚠️ СКОРО: Готовься",
                _ => "✅ ДАЛЕКО: Норм торговля"
            };
            status.AppendLine($"║ FOMC: {fomc:yyyy-MM-dd} ({(int)daysUntil.TotalDays}д) {fomcStatus,-30}║");
        }

        if (macroData.TryGetValue("BTCDominance", out var btcdObj) && btcdObj is decimal btcd)
        {
            string btcdStatus = btcd switch
            {
                > 55 => "🔴 BTC доминирует - Альты слабые",
                > 45 => "🟡 Нейтраль",
                _ => "🟢 Альты растут - Long на альты"
            };
            status.AppendLine($"║ BTC.D: {btcd:F2}%  {btcdStatus,-42}║");
        }

        status.AppendLine("╚════════════════════════════════════════════════════════╝");

        return status.ToString();
    }
}