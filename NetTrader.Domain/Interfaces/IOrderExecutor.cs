using NetTrader.Domain.Entities;

namespace NetTrader.Domain.Interfaces;

public interface IOrderExecutor
{
    /// <summary>Отменяет ВСЕ ордера (лимитные + algo) для символа.</summary>
    Task CancelAllGridOrdersAsync(string symbol);

    /// <summary>
    /// FIX #2: Отменяет только ЛИМИТНЫЕ ордера, оставляя algo SL/TP на месте.
    /// Используется при пере-выставлении сетки, чтобы не оголить позицию.
    /// </summary>
    Task CancelLimitOrdersOnlyAsync(string symbol);

    /// <summary>
    /// FIX #4: Отменяет только ALGO ордера (SL/TP/Trailing).
    /// Используется перед закрытием позиции, чтобы не оставлять orphaned SL.
    /// </summary>
    Task CancelAlgoOrdersAsync(string symbol);

    Task PlaceGridAsync(string symbol, List<GridOrder> orders, decimal stopLoss, decimal takeProfit);
    Task ClosePositionAsync(string symbol);
    Task<decimal> GetMarginBalanceAsync();
    Task<decimal> GetAvailableBalanceAsync();
    Task<(decimal PnL, decimal Amount)> GetPositionInfoAsync(string symbol);
    Task PlaceTrailingStopAsync(string symbol, decimal positionAmount, decimal callbackRate);
    Task EnsureSlTpIfMissingAsync(string symbol, decimal positionAmount, decimal stopLoss, decimal takeProfit);

    /// <summary>Открыть позицию по рынку (market order)</summary>
    Task PlaceMarketOrderAsync(string symbol, string side, decimal quantity);
}