using NetTrader.Domain.Entities;

namespace NetTrader.Domain.Interfaces;

public interface IAiAdvisor
{
    // Теперь метод принимает список кандидатов и возвращает список стратегий
    Task<List<GridSettings>> AnalyzeAndSelectGridsAsync(List<MarketData> candidates, decimal currentBalance);
}