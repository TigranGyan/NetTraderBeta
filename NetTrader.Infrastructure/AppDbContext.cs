using Microsoft.EntityFrameworkCore;
using NetTrader.Domain.Entities;

namespace NetTrader.Infrastructure;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // Это наша таблица в базе данных
    public DbSet<TradeSession> TradeSessions { get; set; }
}