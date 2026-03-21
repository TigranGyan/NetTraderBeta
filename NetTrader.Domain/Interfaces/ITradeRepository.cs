using NetTrader.Domain.Entities;

namespace NetTrader.Domain.Interfaces;

public interface ITradeRepository
{
    Task<List<TradeSession>> GetActiveSessionsAsync();
    Task<TradeSession?> GetActiveSessionAsync(string symbol);
    Task AddSessionAsync(TradeSession session);
    Task UpdateSessionAsync(TradeSession session);
    Task<int> GetActiveGridsCountAsync();
    Task<int> GetTrailingGridsCountAsync();
    Task<int> GetClosedGridsCountSinceAsync(DateTime date);
}