using NetTrader.Domain.Entities;

namespace NetTrader.Domain.Interfaces;

public interface IMarketDataProvider
{
    Task<MarketData> GetCurrentMarketDataAsync(string symbol);
    // Получить список самых волатильных пар
    Task<List<MarketData>> GetTopVolatilityPairsAsync(int limit);

    Task<List<Candle>> GetKLinesAsync(string symbol, string interval = "4h", int limit = 30);
}