using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTrader.Domain.Constants;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Interfaces;
using NetTrader.Domain.Options;

namespace NetTrader.Infrastructure.Exchanges;

public class BinanceFuturesClient : IMarketDataProvider, IOrderExecutor
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly BotState _botState;
    private readonly ILogger<BinanceFuturesClient> _logger;

    private ITelegramService? _telegram;

    // ═══════════════════════════════════════════════════════
    // FIX #14: Static caches — intentionally shared across all
    // scoped instances of BinanceFuturesClient. The client is
    // registered as Scoped via DI, but these caches are static
    // because exchangeInfo and server time offset are global
    // Binance state, not per-request state. Thread safety is
    // ensured via SemaphoreSlim.
    // ═══════════════════════════════════════════════════════
    private static long _serverTimeOffset = 0;
    private static DateTime _lastTimeSync = DateTime.MinValue;
    private static readonly SemaphoreSlim _timeSyncLock = new(1, 1);

    private static ConcurrentDictionary<string, (decimal tick, decimal step, decimal minNotional)>? _exchangeInfoCache;
    private static DateTime _exchangeInfoCacheTime = DateTime.MinValue;
    private static readonly SemaphoreSlim _exchangeInfoLock = new(1, 1);
    private static readonly TimeSpan ExchangeInfoCacheDuration = TimeSpan.FromMinutes(10);

    public BinanceFuturesClient(
        HttpClient httpClient,
        IOptions<BinanceOptions> options,
        BotState botState,
        ILogger<BinanceFuturesClient> logger)
    {
        _httpClient = httpClient;
        _botState = botState;
        _logger = logger;

        var opts = options.Value;
        _apiKey = opts.ApiKey;
        _apiSecret = opts.ApiSecret;
    }

    public void SetTelegramService(ITelegramService telegram) => _telegram = telegram;

    // ==========================================
    // Синхронизация времени
    // ==========================================
    private async Task<long> GetSyncedTimestampAsync()
    {
        if (DateTime.UtcNow - _lastTimeSync > TimeSpan.FromMinutes(5))
            await SyncServerTimeAsync();
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _serverTimeOffset;
    }

    private async Task SyncServerTimeAsync()
    {
        if (!await _timeSyncLock.WaitAsync(0)) return;
        try
        {
            var res = await _httpClient.GetAsync("/fapi/v1/time");
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                long serverTime = doc.RootElement.GetProperty("serverTime").GetInt64();
                long localTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _serverTimeOffset = serverTime - localTime;
                _lastTimeSync = DateTime.UtcNow;
                _logger.LogInformation("⏱ Время синхронизировано. Смещение: {Offset}ms", _serverTimeOffset);
            }
        }
        catch (Exception ex) { _logger.LogWarning("⚠️ Не удалось синхронизировать время: {Error}", ex.Message); }
        finally { _timeSyncLock.Release(); }
    }

    // ==========================================
    // Кэшированный exchangeInfo
    // ==========================================
    private async Task<ConcurrentDictionary<string, (decimal tick, decimal step, decimal minNotional)>> GetExchangeInfoAsync()
    {
        if (_exchangeInfoCache != null && DateTime.UtcNow - _exchangeInfoCacheTime < ExchangeInfoCacheDuration)
            return _exchangeInfoCache;

        await _exchangeInfoLock.WaitAsync();
        try
        {
            if (_exchangeInfoCache != null && DateTime.UtcNow - _exchangeInfoCacheTime < ExchangeInfoCacheDuration)
                return _exchangeInfoCache;

            var infoResponse = await _httpClient.GetAsync("/fapi/v1/exchangeInfo");
            infoResponse.EnsureSuccessStatusCode();
            using var infoDoc = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());

            var newCache = new ConcurrentDictionary<string, (decimal tick, decimal step, decimal minNotional)>();
            foreach (var s in infoDoc.RootElement.GetProperty("symbols").EnumerateArray())
            {
                if (s.GetProperty("status").GetString() != "TRADING") continue;
                var sym = s.GetProperty("symbol").GetString()!;
                decimal tickSize = 0, stepSize = 0, minNotional = 5m;
                foreach (var filter in s.GetProperty("filters").EnumerateArray())
                {
                    switch (filter.GetProperty("filterType").GetString())
                    {
                        case "PRICE_FILTER": tickSize = decimal.Parse(filter.GetProperty("tickSize").GetString()!, CultureInfo.InvariantCulture); break;
                        case "LOT_SIZE": stepSize = decimal.Parse(filter.GetProperty("stepSize").GetString()!, CultureInfo.InvariantCulture); break;
                        case "MIN_NOTIONAL": minNotional = decimal.Parse(filter.GetProperty("notional").GetString()!, CultureInfo.InvariantCulture); break;
                    }
                }
                newCache[sym] = (tickSize, stepSize, minNotional);
            }

            _exchangeInfoCache = newCache;
            _exchangeInfoCacheTime = DateTime.UtcNow;
            _logger.LogInformation("📋 exchangeInfo закэширован ({Count} символов)", newCache.Count);
            return _exchangeInfoCache;
        }
        finally { _exchangeInfoLock.Release(); }
    }

    // ==========================================
    // ПОЛУЧЕНИЕ ДАННЫХ РЫНКА
    // ==========================================
    public async Task<MarketData> GetCurrentMarketDataAsync(string symbol)
    {
        var priceResponse = await _httpClient.GetAsync($"/fapi/v1/ticker/price?symbol={symbol}");
        priceResponse.EnsureSuccessStatusCode();
        using var priceDoc = JsonDocument.Parse(await priceResponse.Content.ReadAsStringAsync());
        decimal currentPrice = decimal.Parse(priceDoc.RootElement.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);

        var exchangeInfo = await GetExchangeInfoAsync();
        if (!exchangeInfo.TryGetValue(symbol, out var filters))
            throw new Exception($"Инструмент {symbol} не найден на Binance Futures.");

        return new MarketData
        {
            Symbol = symbol,
            CurrentPrice = currentPrice,
            TickSize = filters.tick,
            StepSize = filters.step,
            MinNotional = filters.minNotional
        };
    }

    // ══════════════════════════════════════════════════════════
    // ОТМЕНА ОРДЕРОВ — ГРАНУЛЯРНО (FIX #2, #4)
    // ══════════════════════════════════════════════════════════

    /// <summary>Отменяет ВСЕ ордера: лимитные + algo.</summary>
    public async Task CancelAllGridOrdersAsync(string symbol)
    {
        await CancelLimitOrdersOnlyAsync(symbol);
        await CancelAlgoOrdersAsync(symbol);
        _logger.LogInformation("Все ордера для {Symbol} отменены (лимитные + algo).", symbol);
    }

    /// <summary>
    /// FIX #2: Отменяет ТОЛЬКО лимитные ордера, оставляя algo SL/TP.
    /// Используется при пере-выставлении сетки, чтобы позиция оставалась защищённой.
    /// </summary>
    public async Task CancelLimitOrdersOnlyAsync(string symbol)
    {
        try
        {
            var timestamp = await GetSyncedTimestampAsync();
            var queryString = $"symbol={symbol}&timestamp={timestamp}";
            var sig = GenerateSignature(queryString, _apiSecret);
            var req = new HttpRequestMessage(HttpMethod.Delete, $"/fapi/v1/allOpenOrders?{queryString}&signature={sig}");
            req.Headers.Add("X-MBX-APIKEY", _apiKey);
            await _httpClient.SendAsync(req);
            _logger.LogDebug("Лимитные ордера для {Symbol} отменены.", symbol);
        }
        catch (Exception ex) { _logger.LogWarning("Ошибка отмены лимитных ордеров {Symbol}: {E}", symbol, ex.Message); }
    }

    /// <summary>
    /// FIX #4: Отменяет ТОЛЬКО algo ордера (SL/TP/Trailing).
    /// Используется перед закрытием позиции, чтобы orphaned SL не сработал на следующей.
    /// </summary>
    public async Task CancelAlgoOrdersAsync(string symbol)
    {
        try
        {
            var ts = await GetSyncedTimestampAsync();
            var qs = $"symbol={symbol}&timestamp={ts}";
            var sig = GenerateSignature(qs, _apiSecret);
            var req = new HttpRequestMessage(HttpMethod.Delete, $"/fapi/v1/algoOpenOrders?{qs}&signature={sig}");
            req.Headers.Add("X-MBX-APIKEY", _apiKey);
            await _httpClient.SendAsync(req);
            _logger.LogDebug("Algo ордера для {Symbol} отменены.", symbol);
        }
        catch (Exception ex) { _logger.LogWarning("Ошибка отмены algo ордеров {Symbol}: {E}", symbol, ex.Message); }
    }

    // ══════════════════════════════════════════════════════════
    // РАССТАНОВКА СЕТКИ (FIX #3: safe partial-fill rollback)
    // ══════════════════════════════════════════════════════════
    public async Task PlaceGridAsync(string symbol, List<GridOrder> orders, decimal stopLoss, decimal takeProfit)
    {
        _logger.LogInformation("Расстановка сетки {Symbol} ({Count} ордеров)", symbol, orders.Count);

        var timestamp = await GetSyncedTimestampAsync();

        // 1. Изолированная маржа
        try { await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/marginType", $"symbol={symbol}&marginType=ISOLATED&timestamp={timestamp}"); } catch { }

        // 2. Плечо — из BotState (singleton)
        try
        {
            int lev = _botState.Leverage;
            timestamp = await GetSyncedTimestampAsync();
            await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/leverage", $"symbol={symbol}&leverage={lev}&timestamp={timestamp}", silentError: true);
        }
        catch { }

        // 3. Лимитные ордера
        // FIX #3: Отслеживаем успешно выставленные ордера.
        // При ошибке — откатываем лимитные + проверяем заполненные позиции.
        int placedCount = 0;
        try
        {
            foreach (var order in orders)
            {
                await SendOrderAsync(symbol, order.Side, "LIMIT", order.Quantity, order.Price);
                placedCount++;
                await Task.Delay(120);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка сетки {Symbol} после {Placed}/{Total} ордеров. Откат...",
                symbol, placedCount, orders.Count);

            // Отменяем оставшиеся незаполненные лимитные ордера
            await CancelLimitOrdersOnlyAsync(symbol);

            // FIX #3: Проверяем, не заполнились ли уже какие-то ордера (создав позицию)
            var (_, posAmount) = await GetPositionInfoAsync(symbol);
            if (posAmount != 0)
            {
                _logger.LogWarning("⚠️ {Symbol}: Частичное заполнение обнаружено (pos={Amt}). Ставлю аварийный SL/TP.",
                    symbol, posAmount);

                // Ставим SL/TP на частично заполненную позицию — не оставляем её голой
                try
                {
                    var md = await GetCurrentMarketDataAsync(symbol);
                    await PlaceAlgoSlTpWithClosePositionAsync(symbol, stopLoss, takeProfit, md);
                }
                catch (Exception slEx)
                {
                    _logger.LogError(slEx, "❌ Не удалось поставить SL/TP на частичную позицию {Symbol}!", symbol);
                    if (_telegram != null)
                        await _telegram.SendMessageAsync($"🚨 *КРИТИЧНО*: {symbol} имеет открытую позицию ({posAmount}) БЕЗ SL/TP!");
                }
            }

            throw; // Пробрасываем дальше — GridTradingManager должен знать об ошибке
        }

        _logger.LogInformation("✅ Сетка {Symbol} выставлена. SL/TP...", symbol);

        // 4. SL/TP через Algo API
        var marketData = await GetCurrentMarketDataAsync(symbol);
        bool slTpPlaced = await PlaceAlgoSlTpWithClosePositionAsync(symbol, stopLoss, takeProfit, marketData);
        if (slTpPlaced) return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 5 : 10));
            var (hasSl, hasTp) = await GetAlgoSlTpExistsAsync(symbol);
            if (hasSl && hasTp) return;

            if (!hasSl) await PlaceAlgoStopLossAsync(symbol, stopLoss, marketData);
            if (!hasTp) await PlaceAlgoTakeProfitAsync(symbol, takeProfit, marketData);

            (hasSl, hasTp) = await GetAlgoSlTpExistsAsync(symbol);
            if (hasSl && hasTp) return;
        }

        _logger.LogWarning("⚠️ SL/TP для {Symbol} не выставились!", symbol);
        if (_telegram != null)
            await _telegram.SendMessageAsync($"⚠️ *ВНИМАНИЕ*: SL/TP для *{symbol}* не выставились!");
    }

    // ══════════════════════════════════════════════════════════
    // ALGO ORDER API для SL/TP/Trailing
    // ══════════════════════════════════════════════════════════

    private async Task<bool> PlaceAlgoSlTpWithClosePositionAsync(string symbol, decimal stopLoss, decimal takeProfit, MarketData md)
    {
        if (stopLoss <= 0 || takeProfit <= 0) return false;
        stopLoss = RoundToTick(stopLoss, md.TickSize);
        takeProfit = RoundToTick(takeProfit, md.TickSize);

        string slSide = stopLoss < md.CurrentPrice ? "SELL" : "BUY";
        string tpSide = takeProfit > md.CurrentPrice ? "SELL" : "BUY";

        bool slOk = false, tpOk = false;

        try
        {
            var ts = await GetSyncedTimestampAsync();
            var slQuery = $"algoType=CONDITIONAL&symbol={symbol}&side={slSide}&type=STOP_MARKET" +
                          $"&triggerPrice={stopLoss.ToString(CultureInfo.InvariantCulture)}" +
                          $"&closePosition=true&workingType=MARK_PRICE&timestamp={ts}";
            await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/algoOrder", slQuery);
            _logger.LogInformation("🛡 SL (algo) {Symbol}: {Price}", symbol, stopLoss);
            slOk = true;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("-4130")) { _logger.LogInformation("✅ SL для {Symbol} уже на месте (Binance -4130)", symbol); slOk = true; }
            else _logger.LogWarning("⚠️ SL algo не выставлен: {Msg}", ex.Message);
        }

        try
        {
            var ts = await GetSyncedTimestampAsync();
            var tpQuery = $"algoType=CONDITIONAL&symbol={symbol}&side={tpSide}&type=TAKE_PROFIT_MARKET" +
                          $"&triggerPrice={takeProfit.ToString(CultureInfo.InvariantCulture)}" +
                          $"&closePosition=true&workingType=MARK_PRICE&timestamp={ts}";
            await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/algoOrder", tpQuery);
            _logger.LogInformation("🎯 TP (algo) {Symbol}: {Price}", symbol, takeProfit);
            tpOk = true;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("-4130")) { _logger.LogInformation("✅ TP для {Symbol} уже на месте (Binance -4130)", symbol); tpOk = true; }
            else _logger.LogWarning("⚠️ TP algo не выставлен: {Msg}", ex.Message);
        }

        return slOk && tpOk;
    }

    private async Task PlaceAlgoStopLossAsync(string symbol, decimal stopLoss, MarketData md)
    {
        if (stopLoss <= 0) return;
        stopLoss = RoundToTick(stopLoss, md.TickSize);
        string side = stopLoss < md.CurrentPrice ? "SELL" : "BUY";

        try
        {
            var ts = await GetSyncedTimestampAsync();
            var q = $"algoType=CONDITIONAL&symbol={symbol}&side={side}&type=STOP_MARKET" +
                    $"&triggerPrice={stopLoss.ToString(CultureInfo.InvariantCulture)}" +
                    $"&closePosition=true&workingType=MARK_PRICE&timestamp={ts}";
            await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/algoOrder", q);
            _logger.LogInformation("🛡 SL (algo retry) {Symbol}: {Price}", symbol, stopLoss);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("-4130")) _logger.LogInformation("✅ SL для {Symbol} уже на месте", symbol);
            else _logger.LogWarning("⚠️ SL algo не выставлен: {Msg}", ex.Message);
        }
    }

    private async Task PlaceAlgoTakeProfitAsync(string symbol, decimal takeProfit, MarketData md)
    {
        if (takeProfit <= 0) return;
        takeProfit = RoundToTick(takeProfit, md.TickSize);
        string side = takeProfit > md.CurrentPrice ? "SELL" : "BUY";

        try
        {
            var ts = await GetSyncedTimestampAsync();
            var q = $"algoType=CONDITIONAL&symbol={symbol}&side={side}&type=TAKE_PROFIT_MARKET" +
                    $"&triggerPrice={takeProfit.ToString(CultureInfo.InvariantCulture)}" +
                    $"&closePosition=true&workingType=MARK_PRICE&timestamp={ts}";
            await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/algoOrder", q);
            _logger.LogInformation("🎯 TP (algo retry) {Symbol}: {Price}", symbol, takeProfit);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("-4130")) _logger.LogInformation("✅ TP для {Symbol} уже на месте", symbol);
            else _logger.LogWarning("⚠️ TP algo не выставлен: {Msg}", ex.Message);
        }
    }

    private async Task<(bool HasSl, bool HasTp)> GetAlgoSlTpExistsAsync(string symbol)
    {
        bool hasSl = false, hasTp = false;

        try
        {
            var ts = await GetSyncedTimestampAsync();
            var query = $"symbol={symbol}&timestamp={ts}";
            var sig = GenerateSignature(query, _apiSecret);
            var req = new HttpRequestMessage(HttpMethod.Get, $"/fapi/v1/openAlgoOrders?{query}&signature={sig}");
            req.Headers.Add("X-MBX-APIKEY", _apiKey);
            var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode) return (false, false);

            var responseBody = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);

            JsonElement ordersArray;
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("orders", out var ordersNode))
            {
                if (ordersNode.ValueKind != JsonValueKind.Array) return (false, false);
                ordersArray = ordersNode;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                ordersArray = doc.RootElement;
            }
            else return (false, false);

            foreach (var o in ordersArray.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;

                string orderType = "";
                if (o.TryGetProperty("algoOrderType", out var aotProp) && aotProp.ValueKind == JsonValueKind.String)
                    orderType = aotProp.GetString() ?? "";
                else if (o.TryGetProperty("orderType", out var otProp) && otProp.ValueKind == JsonValueKind.String)
                    orderType = otProp.GetString() ?? "";
                else if (o.TryGetProperty("type", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                    orderType = tProp.GetString() ?? "";

                string upperType = orderType.ToUpperInvariant();
                if (upperType.Contains("STOP") && !upperType.Contains("TAKE_PROFIT") && !upperType.Contains("TRAILING"))
                    hasSl = true;
                if (upperType.Contains("TAKE_PROFIT"))
                    hasTp = true;
            }

            _logger.LogDebug("Algo SL/TP check {Symbol}: SL={HasSl}, TP={HasTp}", symbol, hasSl, hasTp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ошибка проверки algo SL/TP для {Symbol}: {E}", symbol, ex.Message);
        }

        return (hasSl, hasTp);
    }

    // ==========================================
    // ENSURE SL/TP
    // ==========================================
    public async Task EnsureSlTpIfMissingAsync(string symbol, decimal positionAmount, decimal stopLoss, decimal takeProfit)
    {
        if (stopLoss <= 0 || takeProfit <= 0 || positionAmount == 0) return;

        var (hasSl, hasTp) = await GetAlgoSlTpExistsAsync(symbol);
        if (hasSl && hasTp)
        {
            _logger.LogDebug("✅ {Symbol}: SL и TP на месте", symbol);
            return;
        }

        _logger.LogWarning("⚠️ {Symbol}: SL={HasSl}, TP={HasTp} — доставляю через algo", symbol, hasSl, hasTp);

        var md = await GetCurrentMarketDataAsync(symbol);

        if (!hasSl && !hasTp)
        {
            bool ok = await PlaceAlgoSlTpWithClosePositionAsync(symbol, stopLoss, takeProfit, md);
            if (ok) return;
        }

        if (!hasSl) await PlaceAlgoStopLossAsync(symbol, stopLoss, md);
        if (!hasTp) await PlaceAlgoTakeProfitAsync(symbol, takeProfit, md);
    }

    // ==========================================
    // TRAILING STOP (algo API)
    // ==========================================
    public async Task PlaceTrailingStopAsync(string symbol, decimal positionAmount, decimal callbackRate)
    {
        string side = positionAmount > 0 ? "SELL" : "BUY";
        decimal quantity = Math.Abs(positionAmount);
        var timestamp = await GetSyncedTimestampAsync();

        string qs = $"algoType=CONDITIONAL&symbol={symbol}&side={side}&type=TRAILING_STOP_MARKET" +
                    $"&quantity={quantity.ToString(CultureInfo.InvariantCulture)}" +
                    $"&callbackRate={callbackRate.ToString(CultureInfo.InvariantCulture)}" +
                    $"&timestamp={timestamp}";
        await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/algoOrder", qs);
        _logger.LogInformation("📈 Trailing (algo) {Symbol}: callback={Rate}%", symbol, callbackRate);
    }

    // ==========================================
    // РЫНОЧНЫЕ ДАННЫЕ — список пар (FIX #6: uses SymbolConfig)
    // ==========================================
    public async Task<List<MarketData>> GetTopVolatilityPairsAsync(int limit)
    {
        var exchangeInfo = await GetExchangeInfoAsync();

        var fundingRates = new Dictionary<string, decimal>();
        try
        {
            var frResponse = await _httpClient.GetAsync("/fapi/v1/premiumIndex");
            using var frDoc = JsonDocument.Parse(await frResponse.Content.ReadAsStringAsync());
            foreach (var item in frDoc.RootElement.EnumerateArray())
            {
                var sym = item.GetProperty("symbol").GetString() ?? "";
                if (decimal.TryParse(item.GetProperty("lastFundingRate").GetString() ?? "0",
                    NumberStyles.Any, CultureInfo.InvariantCulture, out decimal rate))
                    fundingRates[sym] = rate * 100m;
            }
        }
        catch (Exception ex) { _logger.LogWarning("Funding rates: {Error}", ex.Message); }

        var response = await _httpClient.GetAsync("/fapi/v1/ticker/24hr");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // FIX #6: Single source — SymbolConfig.AllowedSymbols
        return doc.RootElement.EnumerateArray()
            .Where(x =>
            {
                var symbol = x.GetProperty("symbol").GetString()!;
                return SymbolConfig.AllowedSymbols.Contains(symbol) && exchangeInfo.ContainsKey(symbol);
            })
            .Select(x =>
            {
                var symbol = x.GetProperty("symbol").GetString()!;
                var filters = exchangeInfo[symbol];
                fundingRates.TryGetValue(symbol, out decimal fundingRate);
                return new MarketData
                {
                    Symbol = symbol,
                    CurrentPrice = decimal.Parse(x.GetProperty("lastPrice").GetString()!, CultureInfo.InvariantCulture),
                    Volatility24h = decimal.Parse(x.GetProperty("priceChangePercent").GetString()!, CultureInfo.InvariantCulture),
                    High24h = decimal.Parse(x.GetProperty("highPrice").GetString()!, CultureInfo.InvariantCulture),
                    Low24h = decimal.Parse(x.GetProperty("lowPrice").GetString()!, CultureInfo.InvariantCulture),
                    TickSize = filters.tick,
                    StepSize = filters.step,
                    MinNotional = filters.minNotional,
                    FundingRate = fundingRate
                };
            })
            .OrderByDescending(x => Math.Abs(x.Volatility24h))
            .Take(limit)
            .ToList();
    }

    public async Task<List<Candle>> GetKLinesAsync(string symbol, string interval = "4h", int limit = 300)
    {
        var response = await _httpClient.GetAsync($"/fapi/v1/klines?symbol={symbol}&interval={interval}&limit={limit}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().Select(c => new Candle
        {
            OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(c[0].GetInt64()).DateTime,
            Open = decimal.Parse(c[1].GetString()!, CultureInfo.InvariantCulture),
            High = decimal.Parse(c[2].GetString()!, CultureInfo.InvariantCulture),
            Low = decimal.Parse(c[3].GetString()!, CultureInfo.InvariantCulture),
            Close = decimal.Parse(c[4].GetString()!, CultureInfo.InvariantCulture),
            Volume = decimal.Parse(c[5].GetString()!, CultureInfo.InvariantCulture)
        }).ToList();
    }

    // ==========================================
    // ПОЗИЦИИ И БАЛАНС
    // ==========================================
    public async Task<(decimal PnL, decimal Amount)> GetPositionInfoAsync(string symbol)
    {
        var timestamp = await GetSyncedTimestampAsync();
        var query = $"symbol={symbol}&timestamp={timestamp}";
        var sig = GenerateSignature(query, _apiSecret);
        var req = new HttpRequestMessage(HttpMethod.Get, $"/fapi/v2/positionRisk?{query}&signature={sig}");
        req.Headers.Add("X-MBX-APIKEY", _apiKey);
        var res = await _httpClient.SendAsync(req);
        if (!res.IsSuccessStatusCode) return (0m, 0m);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var posInfo = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (posInfo.ValueKind == JsonValueKind.Undefined) return (0m, 0m);
        decimal amount = decimal.Parse(posInfo.GetProperty("positionAmt").GetString()!, CultureInfo.InvariantCulture);
        decimal pnl = decimal.Parse(posInfo.GetProperty("unRealizedProfit").GetString()!, CultureInfo.InvariantCulture);
        return (pnl, amount);
    }

    /// <summary>
    /// FIX #4: ClosePositionAsync теперь отменяет algo-ордера перед закрытием.
    /// Без этого orphaned SL мог сработать на следующей позиции по тому же символу.
    /// </summary>
    public async Task ClosePositionAsync(string symbol)
    {
        // FIX #4: Сначала убираем algo SL/TP, чтобы не осталось orphaned ордеров
        await CancelAlgoOrdersAsync(symbol);

        var timestamp = await GetSyncedTimestampAsync();
        var query = $"symbol={symbol}&timestamp={timestamp}";
        var sig = GenerateSignature(query, _apiSecret);
        var req = new HttpRequestMessage(HttpMethod.Get, $"/fapi/v2/positionRisk?{query}&signature={sig}");
        req.Headers.Add("X-MBX-APIKEY", _apiKey);
        var res = await _httpClient.SendAsync(req);
        if (!res.IsSuccessStatusCode) return;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var posInfo = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (posInfo.ValueKind == JsonValueKind.Undefined) return;
        decimal positionAmt = decimal.Parse(posInfo.GetProperty("positionAmt").GetString()!, CultureInfo.InvariantCulture);
        if (positionAmt == 0) return;
        string side = positionAmt > 0 ? "SELL" : "BUY";
        var ts = await GetSyncedTimestampAsync();
        string closeQuery = $"symbol={symbol}&side={side}&type=MARKET&quantity={Math.Abs(positionAmt).ToString(CultureInfo.InvariantCulture)}&reduceOnly=true&timestamp={ts}";
        await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/order", closeQuery);
    }

    public async Task<decimal> GetAvailableBalanceAsync()
    {
        await SyncServerTimeAsync();
        var timestamp = await GetSyncedTimestampAsync();
        var qs = $"recvWindow=60000&timestamp={timestamp}";
        var sig = GenerateSignature(qs, _apiSecret);
        var req = new HttpRequestMessage(HttpMethod.Get, $"/fapi/v2/account?{qs}&signature={sig}");
        req.Headers.Add("X-MBX-APIKEY", _apiKey);
        var response = await _httpClient.SendAsync(req);
        if (!response.IsSuccessStatusCode) return 0m;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return decimal.Parse(doc.RootElement.GetProperty("availableBalance").GetString()!, CultureInfo.InvariantCulture);
    }

    public async Task<decimal> GetMarginBalanceAsync()
    {
        await SyncServerTimeAsync();
        var timestamp = await GetSyncedTimestampAsync();
        var qs = $"recvWindow=60000&timestamp={timestamp}";
        var sig = GenerateSignature(qs, _apiSecret);
        var req = new HttpRequestMessage(HttpMethod.Get, $"/fapi/v2/account?{qs}&signature={sig}");
        req.Headers.Add("X-MBX-APIKEY", _apiKey);
        var response = await _httpClient.SendAsync(req);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            if (error.Contains("-1021"))
            {
                _lastTimeSync = DateTime.MinValue;
                await SyncServerTimeAsync();
                var ts2 = await GetSyncedTimestampAsync();
                var qs2 = $"recvWindow=60000&timestamp={ts2}";
                var sig2 = GenerateSignature(qs2, _apiSecret);
                var req2 = new HttpRequestMessage(HttpMethod.Get, $"/fapi/v2/account?{qs2}&signature={sig2}");
                req2.Headers.Add("X-MBX-APIKEY", _apiKey);
                response = await _httpClient.SendAsync(req2);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Binance Balance Error: {await response.Content.ReadAsStringAsync()}");
            }
            else throw new HttpRequestException($"Binance Balance Error: {error}");
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return decimal.Parse(doc.RootElement.GetProperty("totalMarginBalance").GetString()!, CultureInfo.InvariantCulture);
    }

    // ==========================================
    // MARKET ORDER (FIX #15: polling instead of fixed delay)
    // ==========================================
    public async Task PlaceMarketOrderAsync(string symbol, string side, decimal quantity)
    {
        _logger.LogInformation("🎯 Market {Side} {Symbol}: qty={Qty}", side, symbol, quantity);

        var timestamp = await GetSyncedTimestampAsync();
        try { await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/marginType", $"symbol={symbol}&marginType=ISOLATED&timestamp={timestamp}"); } catch { }
        try
        {
            int lev = _botState.Leverage;
            timestamp = await GetSyncedTimestampAsync();
            await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/leverage", $"symbol={symbol}&leverage={lev}&timestamp={timestamp}", silentError: true);
        }
        catch { }

        timestamp = await GetSyncedTimestampAsync();
        var qs = $"symbol={symbol}&side={side}&type=MARKET" +
                 $"&quantity={quantity.ToString(CultureInfo.InvariantCulture)}" +
                 $"&timestamp={timestamp}";

        await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/order", qs);
        _logger.LogInformation("✅ Market {Side} {Symbol}: qty={Qty} исполнен", side, symbol, quantity);
    }

    /// <summary>
    /// FIX #15: Polling вместо фиксированного Task.Delay(500) для получения позиции
    /// после маркет-ордера. Retries 3 раза с увеличивающимся интервалом.
    /// </summary>
    public async Task<(decimal pnl, decimal amount)> WaitForPositionAsync(string symbol, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300 * (i + 1)));
            var (pnl, amount) = await GetPositionInfoAsync(symbol);
            if (amount != 0) return (pnl, amount);
        }
        _logger.LogWarning("⚠️ {Symbol}: Позиция не появилась после {Retries} попыток", symbol, maxRetries);
        return (0m, 0m);
    }

    // ==========================================
    // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
    // ==========================================
    private async Task SendOrderAsync(string symbol, string side, string type, decimal quantity, decimal? price = null)
    {
        var timestamp = await GetSyncedTimestampAsync();
        var qs = $"symbol={symbol}&side={side}&type={type}&quantity={quantity.ToString(CultureInfo.InvariantCulture)}&timestamp={timestamp}";
        if (type == "LIMIT" && price.HasValue)
            qs += $"&price={price.Value.ToString(CultureInfo.InvariantCulture)}&timeInForce=GTC";
        await SendSignedRequestAsync(HttpMethod.Post, "/fapi/v1/order", qs);
    }

    private async Task SendSignedRequestAsync(HttpMethod method, string endpoint, string queryString, bool silentError = false)
    {
        var signature = GenerateSignature(queryString, _apiSecret);
        var url = $"{endpoint}?{queryString}&signature={signature}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-MBX-APIKEY", _apiKey);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            if (!silentError) _logger.LogError("Binance API Error ({Endpoint}): {Error}", endpoint, error);
            throw new HttpRequestException($"Binance API Error: {error}");
        }
    }

    private static string GenerateSignature(string queryString, string apiSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString))).ToLower();
    }

    private static decimal RoundToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Round(value / step) * step;
    }

    private static decimal RoundToTick(decimal value, decimal tickSize)
    {
        if (tickSize <= 0) return value;
        return Math.Round(value / tickSize) * tickSize;
    }
}