using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTrader.Domain.Constants;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Interfaces;
using NetTrader.Domain.Options;

namespace NetTrader.Infrastructure.AiServices;

public class GeminiAdvisor : IAiAdvisor
{
    private readonly HttpClient _httpClient;
    private readonly MacroServices.MacroAnalyzer _macroAnalyzer;
    private readonly ITradeRepository _tradeRepo;
    private readonly IOrderExecutor _orderExecutor;
    private readonly AiApiOptions _aiOptions;
    private readonly ILogger<GeminiAdvisor> _logger;

    /// <summary>
    /// FIX #1: Таймаут на ОДИН шаг fallback-цепочки.
    /// Ранее единственный CTS на 60 минут покрывал ВСЕ 3 шага,
    /// позволяя одному зависшему вызову заблокировать весь цикл.
    /// Теперь каждый шаг имеет собственный таймаут.
    /// </summary>
    private static readonly TimeSpan PerStepTimeout = TimeSpan.FromMinutes(3);

    public GeminiAdvisor(HttpClient httpClient, MacroServices.MacroAnalyzer macroAnalyzer,
        ITradeRepository tradeRepo, IOrderExecutor orderExecutor,
        IOptions<AiApiOptions> aiOptions, ILogger<GeminiAdvisor> logger)
    {
        _httpClient = httpClient;
        _macroAnalyzer = macroAnalyzer;
        _tradeRepo = tradeRepo;
        _orderExecutor = orderExecutor;
        _logger = logger;
        _aiOptions = aiOptions.Value;
    }

    public async Task<List<GridSettings>> AnalyzeAndSelectGridsAsync(List<MarketData> candidates, decimal currentBalance)
    {
        _logger.LogInformation("🚀 ИИ v7: Grid + Market + Close");

        var macroData = await GetMacroDataWithDefaultsAsync();
        var activeSessions = await _tradeRepo.GetActiveSessionsAsync();

        var positionPnLs = new Dictionary<string, (decimal pnl, decimal amount)>();
        foreach (var session in activeSessions)
        {
            try { positionPnLs[session.Symbol] = await _orderExecutor.GetPositionInfoAsync(session.Symbol); }
            catch { }
        }

        var prompt = BuildGridPrompt(candidates, currentBalance, macroData, activeSessions, positionPnLs);

        var reqSearch = new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, tools = new[] { new { googleSearch = new { } } }, generationConfig = new { temperature = 0.1, maxOutputTokens = 100000 } };
        var reqNoSearch = new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, generationConfig = new { response_mime_type = "application/json", temperature = 0.1, maxOutputTokens = 100000 } };

        string GetUrl(string m) => $"https://generativelanguage.googleapis.com/v1beta/models/{m}:generateContent?key={_aiOptions.ApiKey}";

        // FIX #1: Общий CTS на весь pipeline (для graceful shutdown),
        // но каждый шаг fallback имеет свой жёсткий таймаут через linked CTS.
        using var globalCts = new CancellationTokenSource(TimeSpan.FromMinutes(_aiOptions.TimeoutMinutes));

        try
        {
            string aiText = "";

            // ═══ Step 1: Primary model + Google Search ═══
            aiText = await TryCallModelAsync(
                GetUrl(_aiOptions.PrimaryModel), reqSearch,
                "Pro+Search", globalCts.Token);

            // ═══ Step 2: Primary model without search ═══
            if (string.IsNullOrWhiteSpace(aiText) || !aiText.Contains('['))
            {
                aiText = await TryCallModelAsync(
                    GetUrl(_aiOptions.PrimaryModel), reqNoSearch,
                    "Pro", globalCts.Token);
            }

            // ═══ Step 3: Fallback model ═══
            if (string.IsNullOrWhiteSpace(aiText) || !aiText.Contains('['))
            {
                aiText = await TryCallModelAsync(
                    GetUrl(_aiOptions.FallbackModel), reqNoSearch,
                    "Flash-fallback", globalCts.Token);
            }

            var si = aiText.IndexOf('['); var ei = aiText.LastIndexOf(']');
            string json = (si >= 0 && ei > si) ? aiText.Substring(si, ei - si + 1) : "";
            if (string.IsNullOrEmpty(json)) { _logger.LogWarning("Нет JSON"); return new(); }

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
            var result = (JsonSerializer.Deserialize<List<GridSettings>>(json, opts) ?? new())
                .Where(g => candidates.Any(c => c.Symbol == g.Symbol)).ToList();

            decimal totalMargin = 0;
            var validated = new List<GridSettings>();
            foreach (var grid in result)
            {
                var cand = candidates.FirstOrDefault(c => c.Symbol == grid.Symbol);
                if (cand == null) continue;

                if (grid.OrderType == 3)
                {
                    validated.Add(grid);
                    continue;
                }

                if (grid.OrderType == 1 || grid.OrderType == 2)
                {
                    (_, decimal inv) = CalcParams(cand, currentBalance, macroData);
                    grid.TotalInvestment = inv;
                    totalMargin += inv;
                    if (totalMargin > currentBalance * 0.8m) { totalMargin -= inv; continue; }

                    if (grid.StopLoss <= 0 || grid.TakeProfit <= 0)
                    {
                        decimal atr = cand.Atr > 0 ? cand.Atr : cand.CurrentPrice * 0.02m;
                        if (grid.OrderType == 1)
                        {
                            if (grid.StopLoss <= 0) grid.StopLoss = cand.CurrentPrice - 2.5m * atr;
                            if (grid.TakeProfit <= 0) grid.TakeProfit = cand.CurrentPrice + 3.5m * atr;
                        }
                        else
                        {
                            if (grid.StopLoss <= 0) grid.StopLoss = cand.CurrentPrice + 2.5m * atr;
                            if (grid.TakeProfit <= 0) grid.TakeProfit = cand.CurrentPrice - 3.5m * atr;
                        }
                    }

                    if (grid.OrderType == 1 && grid.StopLoss >= cand.CurrentPrice)
                    {
                        decimal atr = cand.Atr > 0 ? cand.Atr : cand.CurrentPrice * 0.02m;
                        grid.StopLoss = cand.CurrentPrice - 2.5m * atr;
                    }
                    if (grid.OrderType == 2 && grid.StopLoss <= cand.CurrentPrice)
                    {
                        decimal atr = cand.Atr > 0 ? cand.Atr : cand.CurrentPrice * 0.02m;
                        grid.StopLoss = cand.CurrentPrice + 2.5m * atr;
                    }

                    validated.Add(grid);
                    continue;
                }

                (int gl, decimal gridInv) = CalcParams(cand, currentBalance, macroData);
                grid.GridLevels = gl;
                grid.TotalInvestment = gridInv;
                totalMargin += gridInv;
                if (totalMargin > currentBalance * 0.8m) { totalMargin -= gridInv; continue; }
                FixSlTp(grid, cand);
                validated.Add(grid);
            }

            _logger.LogInformation("✅ {C}: {S}", validated.Count,
                string.Join(", ", validated.Select(r => $"{r.Symbol}(OT={r.OrderType})")));
            return validated;
        }
        catch (Exception ex) { _logger.LogError(ex, "ИИ ошибка"); return new(); }
    }

    /// <summary>
    /// FIX #1: Каждый шаг fallback имеет собственный таймаут через linked CTS.
    /// Если шаг зависает дольше PerStepTimeout — он прерывается,
    /// и управление переходит к следующему fallback-шагу.
    /// </summary>
    private async Task<string> TryCallModelAsync(string url, object request, string label, CancellationToken globalCt)
    {
        try
        {
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt);
            stepCts.CancelAfter(PerStepTimeout);

            var response = await _httpClient.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"),
                stepCts.Token);
            response.EnsureSuccessStatusCode();
            return await ExtractText(response, stepCts.Token);
        }
        catch (OperationCanceledException) when (!globalCt.IsCancellationRequested)
        {
            _logger.LogWarning("{Label}: таймаут ({Timeout}s)", label, PerStepTimeout.TotalSeconds);
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Label}: {E}", label, ex.Message);
            return "";
        }
    }

    // ═══════════════════════════════════════════════════════════
    // ПРОМПТ v7.1 (FIX #7: uses SymbolConfig.CoinGroups)
    // ═══════════════════════════════════════════════════════════
    private string BuildGridPrompt(List<MarketData> candidates, decimal currentBalance,
        Dictionary<string, object> macroData, List<TradeSession> activeSessions,
        Dictionary<string, (decimal pnl, decimal amount)> positionPnLs)
    {
        var sb = new StringBuilder();
        int fgi = (int)(macroData.ContainsKey("FearGreedIndex") ? macroData["FearGreedIndex"] : 50);

        sb.AppendLine("You are an autonomous AI crypto trader for Binance USDT-M Futures.");
        sb.AppendLine("You have FULL CONTROL over trading decisions. Return a JSON array of actions.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL RULE: You MUST return at least 1 action. Empty [] is FORBIDDEN.");
        sb.AppendLine("Your job is to find the BEST opportunity among these coins, not to avoid all risk.");
        sb.AppendLine("Every coin has some risk — your SL protects you. Pick the best risk/reward.");
        sb.AppendLine();

        string fgiLabel = fgi switch { > 75 => "EXTREME GREED", > 60 => "GREED", >= 40 and <= 60 => "NEUTRAL", > 25 => "FEAR", _ => "EXTREME FEAR" };
        sb.AppendLine($"MACRO: FGI={fgi} ({fgiLabel}), Balance={currentBalance:F2}$");
        if (macroData["FOMCDate"] is DateTime fomcDate)
            sb.AppendLine($"FOMC: {fomcDate:yyyy-MM-dd} ({(int)(fomcDate.Date - DateTime.UtcNow.Date).TotalDays}d)");
        if (macroData["BTCDominance"] is decimal btcD)
            sb.AppendLine($"BTC.D: {btcD:F1}%");
        sb.AppendLine();

        if (activeSessions.Count > 0)
        {
            sb.AppendLine("YOUR OPEN POSITIONS:");
            foreach (var s in activeSessions)
            {
                string pnlStr = positionPnLs.TryGetValue(s.Symbol, out var p)
                    ? $"PnL={p.pnl:+0.00;-0.00}$ Pos={p.amount}"
                    : "";
                sb.AppendLine($"  {s.Symbol} [{s.Status}] Range:{s.LowerPrice}-{s.UpperPrice} SL:{s.StopLoss:F2} TP:{s.TakeProfit:F2} {pnlStr}");
            }
            sb.AppendLine("You can KEEP (include in response), UPDATE (change params), or CLOSE (OrderType=3) any position.");
            sb.AppendLine();
        }

        sb.AppendLine("═══ YOUR TOOLS (OrderType field) ═══");
        sb.AppendLine();
        sb.AppendLine("OrderType=0: GRID — Best for ADX<20. Set LowerPrice, UpperPrice, Direction(0=Long,1=Short,2=Both).");
        sb.AppendLine("OrderType=1: MARKET LONG — Buy now. SL MUST be BELOW price, TP ABOVE. Set LowerPrice/UpperPrice/GridLevels=0.");
        sb.AppendLine("OrderType=2: MARKET SHORT — Sell now. SL MUST be ABOVE price, TP BELOW. Set LowerPrice/UpperPrice/GridLevels=0.");
        sb.AppendLine("OrderType=3: CLOSE — Close existing position. Only need Symbol and Rationale.");
        sb.AppendLine();

        sb.AppendLine("═══ DECISION FRAMEWORK ═══");
        sb.AppendLine("ADX < 20 → GRID (range-bound, grid captures bounces)");
        sb.AppendLine("ADX 20-25 + signal → MARKET (clear direction)");
        sb.AppendLine("ADX > 25 + signal → MARKET (trending)");
        sb.AppendLine("ADX > 25 + no signal → pick lowest ADX coin for GRID instead");
        sb.AppendLine();

        sb.AppendLine("═══ SAFETY ═══");
        sb.AppendLine("- SL and TP ALWAYS required. GRID: SL=3.5×ATR beyond edge. MARKET: SL=2×ATR.");
        sb.AppendLine($"- Total investment ≤ {currentBalance * 0.8m:F0}$ (80% of balance).");
        // FIX #17: Prompt и валидатор теперь синхронизированы — максимум 3 действия
        sb.AppendLine("- Max 3 actions per cycle. Max 1 per correlation group.");
        sb.AppendLine("- |FundingRate| > 0.1% → SKIP that coin. VolumeRatio < 0.15 → SKIP that coin.");
        sb.AppendLine();

        sb.AppendLine("═══ GOOGLE SEARCH RULES ═══");
        sb.AppendLine("Use Google Search to check for BREAKING NEWS only (last 48 hours).");
        sb.AppendLine("SKIP a coin ONLY if you find ALL of these:");
        sb.AppendLine("  1. The incident happened in the LAST 48 HOURS (not weeks/months/years ago)");
        sb.AppendLine("  2. It DIRECTLY affects the token's smart contract, bridge, or treasury");
        sb.AppendLine("  3. Funds are CURRENTLY at risk or ACTIVELY being drained");
        sb.AppendLine();
        sb.AppendLine("DO NOT skip coins for:");
        sb.AppendLine("  - Old hacks (Ronin 2022, Moonwell 2024, etc.) — already priced in");
        sb.AppendLine("  - General SEC regulatory news or ongoing lawsuits — this is normal for crypto");
        sb.AppendLine("  - Third-party protocol hacks that used the token");
        sb.AppendLine("  - FUD articles, opinion pieces, or price predictions");
        sb.AppendLine("  - News older than 48 hours");
        sb.AppendLine();
        sb.AppendLine("If Google Search finds NOTHING alarming in last 48h → the coin is SAFE to trade.");
        sb.AppendLine("When in doubt → TRADE with tighter SL, do NOT skip.");
        sb.AppendLine();

        if (fgi < 25) sb.AppendLine("⚡ FGI < 25: AGGRESSIVE Long entries. Fear = opportunity. Buy the dip!");
        else if (fgi > 75) sb.AppendLine("⚡ FGI > 75: DEFENSIVE. Prefer Short or tight grids.");
        sb.AppendLine();

        sb.AppendLine("GROUPS: [L1]ETH,SOL,AVAX,ADA [L2]ARB,OP [DeFi]LINK,AAVE,UNI [Meme]DOGE,SHIB,PEPE,FLOKI [Exch]BNB,FTM [AI]WLD");
        sb.AppendLine();

        sb.AppendLine("MARKET DATA (sorted by ADX — best grid candidates first):");
        foreach (var c in candidates.OrderBy(c => c.Adx))
        {
            // FIX #7: Single source — SymbolConfig.CoinGroups
            string g = SymbolConfig.CoinGroups.TryGetValue(c.Symbol, out var grp) ? grp : "?";
            decimal emaTrend = c.Ema200 > 0 ? ((c.CurrentPrice - c.Ema200) / c.Ema200) * 100m : 0m;
            string adxLabel = c.Adx < 20 ? "✅RNG" : c.Adx < 25 ? "⚠️MED" : "❌TRD";

            sb.Append($"  {c.Symbol}[{g}] P={c.CurrentPrice}");
            sb.Append($" 24h=[{c.Low24h}-{c.High24h}]({c.Range24hPercent:F1}%)");
            sb.Append($" ADX={c.Adx:F0}{adxLabel}");
            sb.Append($" RSI4h={c.Rsi:F0} RSI1h={c.Rsi1h:F0}");
            sb.Append($" MACD4h={c.MacdHistogram:F4} MACD1h={c.MacdHist1h:F4}");
            sb.Append($" ATR={c.Atr:F4} BB1hPos={c.BbPosition1h:F2}");
            sb.Append($" VR={c.VolumeRatio:F2} FR={c.FundingRate:F4}%");
            sb.Append($" EMA={emaTrend:+0.0;-0.0}%");
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("═══ JSON FORMAT ═══");
        sb.AppendLine("Grid:");
        sb.AppendLine(@"  {""Symbol"":""LINKUSDT"",""UpperPrice"":9.2,""LowerPrice"":8.7,""GridLevels"":10,""TotalInvestment"":25,""Direction"":0,""StopLoss"":8.1,""TakeProfit"":10.0,""OrderType"":0,""Rationale"":""ADX=12 range, FGI=18 fear buy""}");
        sb.AppendLine("Market Long:");
        sb.AppendLine(@"  {""Symbol"":""SOLUSDT"",""UpperPrice"":0,""LowerPrice"":0,""GridLevels"":0,""TotalInvestment"":0,""Direction"":0,""StopLoss"":82.0,""TakeProfit"":92.0,""OrderType"":1,""Rationale"":""RSI1h=28 oversold bounce""}");
        sb.AppendLine("Close:");
        sb.AppendLine(@"  {""Symbol"":""XRPUSDT"",""UpperPrice"":0,""LowerPrice"":0,""GridLevels"":0,""TotalInvestment"":0,""Direction"":0,""StopLoss"":0,""TakeProfit"":0,""OrderType"":3,""Rationale"":""Active bridge hack draining funds NOW""}");
        sb.AppendLine();
        sb.AppendLine("REMEMBER: Return at least 1 action. All reasoning in Rationale field.");

        return sb.ToString();
    }

    private async Task<string> ExtractText(HttpResponseMessage r, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        var sb = new StringBuilder();
        if (!doc.RootElement.TryGetProperty("candidates", out var arr)) return "";
        foreach (var c in arr.EnumerateArray())
            if (c.TryGetProperty("content", out var con) && con.TryGetProperty("parts", out var parts))
                foreach (var p in parts.EnumerateArray())
                    if (p.TryGetProperty("text", out var t)) { var x = t.GetString(); if (!string.IsNullOrEmpty(x)) sb.Append(x); }
        return sb.ToString();
    }

    private void FixSlTp(GridSettings g, MarketData c)
    {
        decimal w = g.UpperPrice - g.LowerPrice, center = (g.UpperPrice + g.LowerPrice) / 2m;
        decimal atr = c.Atr > 0 ? c.Atr : w * 0.05m;
        if (atr <= 0) atr = c.CurrentPrice * 0.02m;

        if (g.StopLoss <= 0 || g.TakeProfit <= 0)
        {
            if (g.Direction == 0) { g.StopLoss = g.LowerPrice - 3.5m * atr; g.TakeProfit = center + 2.5m * w; }
            else if (g.Direction == 1) { g.StopLoss = g.UpperPrice + 3.5m * atr; g.TakeProfit = center - 2.5m * w; }
            else { g.StopLoss = g.LowerPrice - 3.5m * atr; g.TakeProfit = g.UpperPrice + 2.0m * atr; }
        }
        if (g.Direction == 0 && g.StopLoss >= g.LowerPrice) g.StopLoss = g.LowerPrice - 3.5m * atr;
        if (g.Direction == 1 && g.StopLoss <= g.UpperPrice) g.StopLoss = g.UpperPrice + 3.5m * atr;
        if (g.TakeProfit <= 0) g.TakeProfit = g.Direction == 0 ? g.UpperPrice * 1.05m : g.LowerPrice * 0.95m;
        if (g.StopLoss <= 0) g.StopLoss = g.Direction == 0 ? g.LowerPrice * 0.9m : g.UpperPrice * 1.1m;
    }

    private (int, decimal) CalcParams(MarketData c, decimal bal, Dictionary<string, object> macro)
    {
        decimal vm = c.Volatility24h switch { < 1m => 1.4m, < 2m => 1.2m, < 4m => 1.0m, < 7m => 0.85m, < 10m => 0.75m, _ => 0.7m };
        int gl = Math.Max(10, Math.Min(20, (int)(14 * vm)));

        int fgi = (int)(macro.ContainsKey("FearGreedIndex") ? macro["FearGreedIndex"] : 50);
        decimal fm = fgi switch { > 75 => 0.7m, > 60 => 0.85m, >= 40 and <= 60 => 1.0m, > 25 => 1.2m, _ => 1.4m };
        decimal bm = bal switch { > 100m => 1.3m, > 50m => 1.0m, > 25m => 0.8m, _ => 0.5m };
        return (gl, Math.Round(Math.Max(15m, Math.Min(30m, 22m * fm * bm)), 1));
    }

    private async Task<Dictionary<string, object>> GetMacroDataWithDefaultsAsync()
    {
        var r = new Dictionary<string, object> { { "FearGreedIndex", 50 }, { "FOMCDate", "No meeting" }, { "BTCDominance", 50m } };
        try { var raw = await _macroAnalyzer.GetAllMacroDataAsync(); if (raw.TryGetValue("FearGreedIndex", out var f) && f is int fi) r["FearGreedIndex"] = fi; if (raw.TryGetValue("FOMCDate", out var fo) && fo is DateTime d) r["FOMCDate"] = d; if (raw.TryGetValue("BTCDominance", out var b) && b is decimal bd) r["BTCDominance"] = bd; } catch { }
        return r;
    }
}