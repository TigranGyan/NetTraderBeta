using System.Text;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTrader.Domain.Constants;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Interfaces;
using NetTrader.Domain.Options;
using NetTrader.Application.Calculations;

namespace NetTrader.Application.Services;

public class GridTradingManager
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IOrderExecutor _orderExecutor;
    private readonly ILogger<GridTradingManager> _logger;
    private readonly ITelegramService _telegram;
    private readonly ITradeRepository _tradeRepo;
    private readonly TradingOptions _tradingOptions;
    private readonly IValidator<GridSettings> _gridValidator;
    private readonly IValidator<List<GridSettings>> _listValidator;

    public GridTradingManager(
        IMarketDataProvider marketDataProvider,
        IOrderExecutor orderExecutor,
        ILogger<GridTradingManager> logger,
        ITelegramService telegram,
        ITradeRepository tradeRepo,
        IOptions<TradingOptions> tradingOptions,
        IValidator<GridSettings> gridValidator,
        IValidator<List<GridSettings>> listValidator)
    {
        _marketDataProvider = marketDataProvider;
        _orderExecutor = orderExecutor;
        _logger = logger;
        _telegram = telegram;
        _tradeRepo = tradeRepo;
        _tradingOptions = tradingOptions.Value;
        _gridValidator = gridValidator;
        _listValidator = listValidator;
    }

    public async Task UpdateGridsAsync(List<GridSettings> recommendedGrids)
    {
        // ═══════════════════════════════════════
        // ВАЛИДАЦИЯ ИИ (Sanity Checks)
        // ═══════════════════════════════════════
        var listValidation = await _listValidator.ValidateAsync(recommendedGrids);
        if (!listValidation.IsValid)
        {
            _logger.LogWarning("⚠️ Батч от ИИ не прошёл валидацию: {Errors}",
                string.Join("; ", listValidation.Errors.Select(e => e.ErrorMessage)));
            await _telegram.SendMessageAsync($"⚠️ ИИ выдал невалидные данные:\n{string.Join("\n", listValidation.Errors.Select(e => $"• {e.ErrorMessage}"))}");
        }

        var validGrids = new List<GridSettings>();
        foreach (var grid in recommendedGrids)
        {
            var result = await _gridValidator.ValidateAsync(grid);
            if (result.IsValid)
            {
                validGrids.Add(grid);
            }
            else
            {
                _logger.LogWarning("❌ {Symbol} (OT={OrderType}) отклонён: {Errors}",
                    grid.Symbol, grid.OrderType,
                    string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
            }
        }

        if (validGrids.Count == 0 && recommendedGrids.Count > 0)
        {
            _logger.LogError("🚨 ВСЕ {Count} рекомендаций от ИИ отклонены валидацией!", recommendedGrids.Count);
            await _telegram.SendMessageAsync("🚨 Все рекомендации ИИ отклонены валидацией. Цикл пропущен.");
            return;
        }

        recommendedGrids = validGrids;

        var report = new StringBuilder();
        report.AppendLine("🤖 *ИИ обновил портфель!*");

        var activeSessions = await _tradeRepo.GetActiveSessionsAsync();
        var activeSymbols = activeSessions.Select(s => s.Symbol).ToList();
        var recommendedSymbols = recommendedGrids
            .Where(g => g.OrderType != 3)
            .Select(g => g.Symbol).ToList();

        // ════════════════════════════════════════
        // 0. ИИ ЯВНО ПРОСИТ ЗАКРЫТЬ ПОЗИЦИЮ (OrderType=3)
        // ════════════════════════════════════════
        foreach (var action in recommendedGrids.Where(g => g.OrderType == 3))
        {
            var session = activeSessions.FirstOrDefault(s => s.Symbol == action.Symbol);
            if (session == null) continue;

            _logger.LogInformation("🤖 ИИ просит закрыть {Symbol}: {Reason}", action.Symbol, action.Rationale);
            try
            {
                // FIX #4: CancelAllGridOrdersAsync уже отменяет и лимитные, и algo
                await _orderExecutor.CancelAllGridOrdersAsync(action.Symbol);
                await _orderExecutor.ClosePositionAsync(action.Symbol);

                session.Status = TradeStatus.ClosedByAI; // FIX #11
                session.ClosedAt = DateTime.UtcNow;
                await _tradeRepo.UpdateSessionAsync(session);
                report.AppendLine($"🤖 *{action.Symbol}* закрыт по решению ИИ.");
                if (!string.IsNullOrEmpty(action.Rationale))
                    report.AppendLine($"   _{action.Rationale}_");
            }
            catch (Exception ex) { _logger.LogError(ex, "Ошибка закрытия {S}", action.Symbol); }
        }

        activeSessions = await _tradeRepo.GetActiveSessionsAsync();
        activeSymbols = activeSessions.Select(s => s.Symbol).ToList();

        // ════════════════════════════════════════
        // 1. Trailing-проверка
        // ════════════════════════════════════════
        foreach (var session in activeSessions.Where(s => s.Status == TradeStatus.Trailing).ToList())
        {
            try
            {
                var (_, amount) = await _orderExecutor.GetPositionInfoAsync(session.Symbol);
                if (amount == 0)
                {
                    session.Status = TradeStatus.Closed;
                    session.ClosedAt = DateTime.UtcNow;
                    await _tradeRepo.UpdateSessionAsync(session);
                    await _telegram.SendMessageAsync($"✅ *{session.Symbol}* трейлинг сработал!");
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Trailing {S}", session.Symbol); }
        }

        activeSessions = await _tradeRepo.GetActiveSessionsAsync();
        activeSymbols = activeSessions.Select(s => s.Symbol).ToList();

        // 1.5 Ensure SL/TP
        foreach (var session in activeSessions.Where(s => s.Status != TradeStatus.Trailing))
        {
            try
            {
                var (_, posAmt) = await _orderExecutor.GetPositionInfoAsync(session.Symbol);
                if (posAmt != 0 && session.StopLoss > 0 && session.TakeProfit > 0)
                    await _orderExecutor.EnsureSlTpIfMissingAsync(session.Symbol, posAmt, session.StopLoss, session.TakeProfit);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "EnsureSLTP {S}", session.Symbol); }
        }

        // ════════════════════════════════════════
        // 2. Закрытие пар не в рекомендациях
        // ════════════════════════════════════════
        foreach (var symbol in activeSymbols.Except(recommendedSymbols).ToList())
        {
            var session = activeSessions.First(s => s.Symbol == symbol);
            if (session.Status == TradeStatus.Trailing) continue;

            var (currentPnL, positionAmount) = await _orderExecutor.GetPositionInfoAsync(symbol);
            decimal investment = session.TotalInvestment > 0 ? session.TotalInvestment : 20m;
            decimal roiPercent = investment > 0 ? (currentPnL / investment) * 100m : 0m;

            if (roiPercent >= _tradingOptions.TrailingActivationRoiPercent && positionAmount != 0)
            {
                try
                {
                    await _orderExecutor.PlaceTrailingStopAsync(symbol, positionAmount, _tradingOptions.TrailingCallbackRatePercent);
                    // FIX #2: Отменяем только лимитные, trailing уже стоит
                    await _orderExecutor.CancelLimitOrdersOnlyAsync(symbol);
                    // FIX #4: Отменяем старые algo SL/TP (trailing их заменяет)
                    await _orderExecutor.CancelAlgoOrdersAsync(symbol);
                    session.Status = TradeStatus.Trailing;
                    session.ClosedAt = DateTime.UtcNow;
                    await _tradeRepo.UpdateSessionAsync(session);
                    report.AppendLine($"🚀 *{symbol}* +{roiPercent:F1}% → трейлинг!");
                }
                catch { report.AppendLine($"⚠️ *{symbol}* трейлинг ошибка."); }
                continue;
            }

            if (roiPercent < _tradingOptions.MinRoiPercentToCloseWhenAiDrops)
            {
                report.AppendLine($"⏳ *{symbol}* {roiPercent:F1}%. Ждём SL/TP.");
                continue;
            }

            if (positionAmount == 0)
            {
                await _orderExecutor.CancelAllGridOrdersAsync(symbol);
                session.Status = TradeStatus.Closed;
                session.ClosedAt = DateTime.UtcNow;
                await _tradeRepo.UpdateSessionAsync(session);
                continue;
            }

            // FIX #4: CancelAllGridOrdersAsync now handles algo cleanup too
            await _orderExecutor.CancelAllGridOrdersAsync(symbol);
            await _orderExecutor.ClosePositionAsync(symbol);
            session.Status = TradeStatus.Closed;
            session.ClosedAt = DateTime.UtcNow;
            await _tradeRepo.UpdateSessionAsync(session);
            report.AppendLine($"{(currentPnL >= 0 ? "✅" : "❌")} *{symbol}* PnL: {currentPnL:+0.00;-0.00}$");
        }

        // ════════════════════════════════════════
        // 3. ОТКРЫТИЕ — Grid, MarketLong, MarketShort
        // ════════════════════════════════════════
        decimal availableMargin;
        try { availableMargin = await _orderExecutor.GetAvailableBalanceAsync(); }
        catch { availableMargin = decimal.MaxValue; }
        decimal usedMargin = 0;

        foreach (var settings in recommendedGrids.Where(g => g.OrderType != 3))
        {
            var existingSession = activeSessions.FirstOrDefault(s => s.Symbol == settings.Symbol);
            if (existingSession?.Status == TradeStatus.Trailing) continue;

            if (existingSession != null && settings.OrderType == 0)
            {
                var lDiff = Math.Abs(existingSession.LowerPrice - settings.LowerPrice) / Math.Max(existingSession.LowerPrice, 0.01m);
                var uDiff = Math.Abs(existingSession.UpperPrice - settings.UpperPrice) / Math.Max(existingSession.UpperPrice, 0.01m);
                if (lDiff < 0.01m && uDiff < 0.01m) continue;
            }

            if (usedMargin + settings.TotalInvestment > availableMargin * _tradingOptions.MaxMarginUsagePercent)
            {
                report.AppendLine($"⚠️ *{settings.Symbol}* пропущен (маржа).");
                continue;
            }

            // ═══════════════════════════════════════
            // FIX #9: Идемпотентность — сохраняем сессию ПЕРЕД выставлением ордеров
            // со статусом Pending. При успехе → Active. При ошибке → не теряем трекинг.
            // ═══════════════════════════════════════
            string targetStatus = settings.OrderType == 0 ? TradeStatus.Active : TradeStatus.ActiveMarket;
            TradeSession? sessionToTrack = existingSession;

            if (sessionToTrack == null)
            {
                sessionToTrack = new TradeSession
                {
                    Symbol = settings.Symbol,
                    OpenedAt = DateTime.UtcNow,
                    LowerPrice = settings.LowerPrice,
                    UpperPrice = settings.UpperPrice,
                    TotalInvestment = settings.TotalInvestment,
                    StopLoss = settings.StopLoss,
                    TakeProfit = settings.TakeProfit,
                    Status = TradeStatus.Pending // FIX #9
                };
                await _tradeRepo.AddSessionAsync(sessionToTrack);
            }
            else
            {
                sessionToTrack.LowerPrice = settings.LowerPrice;
                sessionToTrack.UpperPrice = settings.UpperPrice;
                sessionToTrack.TotalInvestment = settings.TotalInvestment;
                sessionToTrack.StopLoss = settings.StopLoss;
                sessionToTrack.TakeProfit = settings.TakeProfit;
                sessionToTrack.Status = TradeStatus.Pending;
                await _tradeRepo.UpdateSessionAsync(sessionToTrack);
            }

            try
            {
                switch (settings.OrderType)
                {
                    case 0:
                        // FIX #2: Cancel only limit orders — keep algo SL/TP alive during re-grid
                        await _orderExecutor.CancelLimitOrdersOnlyAsync(settings.Symbol);
                        var marketData = await _marketDataProvider.GetCurrentMarketDataAsync(settings.Symbol);
                        var orders = GridMathCalculator.CalculateOrders(settings, marketData);

                        if (orders.Count == 0) { _logger.LogWarning("{S}: 0 ордеров.", settings.Symbol); continue; }

                        _logger.LogInformation("📊 {S}: GRID {C} ордеров", settings.Symbol, orders.Count);
                        await _orderExecutor.PlaceGridAsync(settings.Symbol, orders, settings.StopLoss, settings.TakeProfit);
                        usedMargin += settings.TotalInvestment;

                        report.AppendLine($"🕸 *{settings.Symbol}* Grid [{settings.LowerPrice}–{settings.UpperPrice}] {orders.Count} lvls");
                        break;

                    case 1:
                        await ExecuteMarketOrder(settings, "BUY", report);
                        usedMargin += settings.TotalInvestment;
                        break;

                    case 2:
                        await ExecuteMarketOrder(settings, "SELL", report);
                        usedMargin += settings.TotalInvestment;
                        break;
                }

                // FIX #9: Pending → Active после успеха
                sessionToTrack.Status = targetStatus;
                await _tradeRepo.UpdateSessionAsync(sessionToTrack);

                if (!string.IsNullOrEmpty(settings.Rationale))
                    report.AppendLine($"   _{settings.Rationale}_");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка открытия {S} (OrderType={OT})", settings.Symbol, settings.OrderType);
                report.AppendLine($"❌ *{settings.Symbol}* ошибка: {ex.Message[..Math.Min(100, ex.Message.Length)]}");

                // FIX #9: При ошибке проверяем, есть ли позиция (partial fill).
                // Если есть — оставляем Pending (сессия трекается). Если нет — закрываем.
                try
                {
                    var (_, posAmt) = await _orderExecutor.GetPositionInfoAsync(settings.Symbol);
                    if (posAmt == 0 && existingSession == null)
                    {
                        // Нет позиции и это была новая сессия — убираем Pending
                        sessionToTrack.Status = TradeStatus.Closed;
                        sessionToTrack.ClosedAt = DateTime.UtcNow;
                        await _tradeRepo.UpdateSessionAsync(sessionToTrack);
                    }
                    // Если posAmt != 0 — позиция существует, оставляем Pending.
                    // PlaceGridAsync (FIX #3) уже поставил аварийный SL/TP.
                }
                catch { /* best effort */ }
            }
        }

        await _telegram.SendMessageAsync(report.ToString());
    }

    private async Task ExecuteMarketOrder(GridSettings settings, string side, StringBuilder report)
    {
        var marketData = await _marketDataProvider.GetCurrentMarketDataAsync(settings.Symbol);

        decimal quantity = settings.TotalInvestment / marketData.CurrentPrice;
        quantity = RoundToStep(quantity, marketData.StepSize);

        if (quantity <= 0)
        {
            _logger.LogWarning("{S}: quantity=0 для маркет-ордера.", settings.Symbol);
            return;
        }

        decimal notional = quantity * marketData.CurrentPrice;
        if (notional < marketData.MinNotional)
        {
            quantity = Math.Ceiling(marketData.MinNotional / marketData.CurrentPrice / marketData.StepSize) * marketData.StepSize;
            _logger.LogInformation("{S}: увеличен qty до MinNotional: {Q}", settings.Symbol, quantity);
        }

        string dirLabel = side == "BUY" ? "📈 MARKET LONG" : "📉 MARKET SHORT";
        _logger.LogInformation("{Label} {Symbol}: qty={Qty} @ ~{Price}",
            dirLabel, settings.Symbol, quantity, marketData.CurrentPrice);

        await _orderExecutor.PlaceMarketOrderAsync(settings.Symbol, side, quantity);

        // FIX #15: Polling вместо фиксированного Task.Delay(500)
        if (settings.StopLoss > 0 && settings.TakeProfit > 0)
        {
            decimal posAmt = 0;
            // Пытаемся получить реальную позицию с retry
            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (i + 1)));
                var (_, amt) = await _orderExecutor.GetPositionInfoAsync(settings.Symbol);
                if (amt != 0)
                {
                    posAmt = amt;
                    break;
                }
            }

            decimal absQty = posAmt != 0 ? Math.Abs(posAmt) : quantity;

            try
            {
                await _orderExecutor.EnsureSlTpIfMissingAsync(
                    settings.Symbol, posAmt != 0 ? posAmt : (side == "BUY" ? absQty : -absQty),
                    settings.StopLoss, settings.TakeProfit);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("⚠️ SL/TP для маркет-ордера {S}: {E}", settings.Symbol, ex.Message);
            }
        }

        report.AppendLine($"{dirLabel} *{settings.Symbol}* qty={quantity} SL:{settings.StopLoss:F2} TP:{settings.TakeProfit:F2}");
    }

    private static decimal RoundToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Round(value / step) * step;
    }
}