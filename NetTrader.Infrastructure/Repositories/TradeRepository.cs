using Microsoft.EntityFrameworkCore;
using NetTrader.Domain.Constants;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Interfaces;

namespace NetTrader.Infrastructure.Repositories;

public class TradeRepository : ITradeRepository
{
    private readonly AppDbContext _db;

    public TradeRepository(AppDbContext db)
    {
        _db = db;
    }

    // FIX #11: Magic strings → TradeStatus constants
    public async Task<TradeSession?> GetActiveSessionAsync(string symbol)
    {
        return await _db.TradeSessions
            .FirstOrDefaultAsync(s => s.Symbol == symbol &&
                (s.Status == TradeStatus.Active ||
                 s.Status == TradeStatus.ActiveMarket ||
                 s.Status == TradeStatus.Trailing ||
                 s.Status == TradeStatus.Pending));
    }

    public async Task<List<TradeSession>> GetActiveSessionsAsync()
    {
        return await _db.TradeSessions
            .Where(s => s.Status == TradeStatus.Active ||
                        s.Status == TradeStatus.ActiveMarket ||
                        s.Status == TradeStatus.Trailing ||
                        s.Status == TradeStatus.Pending)
            .ToListAsync();
    }

    public async Task AddSessionAsync(TradeSession session)
    {
        _db.TradeSessions.Add(session);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateSessionAsync(TradeSession session)
    {
        _db.TradeSessions.Update(session);
        await _db.SaveChangesAsync();
    }

    public async Task<int> GetActiveGridsCountAsync()
    {
        return await _db.TradeSessions
            .CountAsync(s => s.Status == TradeStatus.Active ||
                             s.Status == TradeStatus.ActiveMarket ||
                             s.Status == TradeStatus.Trailing ||
                             s.Status == TradeStatus.Pending);
    }

    public async Task<int> GetTrailingGridsCountAsync()
    {
        return await _db.TradeSessions
            .CountAsync(s => s.Status == TradeStatus.Trailing);
    }

    public async Task<int> GetClosedGridsCountSinceAsync(DateTime date)
    {
        return await _db.TradeSessions
            .CountAsync(s => s.Status == TradeStatus.Closed && s.ClosedAt >= date);
    }
}