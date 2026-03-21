using NetTrader.Domain.Enums;

namespace NetTrader.Domain.Entities;

public class GridSettings
{
    public string Symbol { get; set; } = string.Empty;
    public decimal UpperPrice { get; set; }
    public decimal LowerPrice { get; set; }
    public int GridLevels { get; set; }
    public decimal TotalInvestment { get; set; }

    // Direction: 0=Long, 1=Short, 2=Both
    public int Direction { get; set; }

    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }

    public string? Rationale { get; set; }

    // ═══ NEW: OrderType — ИИ выбирает ТИП действия ═══
    // 0 = Grid (сетка ордеров — для боковика)
    // 1 = MarketLong (открыть Long по рынку — для сильного бычьего сигнала)
    // 2 = MarketShort (открыть Short по рынку — для сильного медвежьего сигнала)
    // 3 = ClosePosition (закрыть существующую позицию — ИИ считает что пора выходить)
    public int OrderType { get; set; } = 0;

    public decimal GridStep => (UpperPrice - LowerPrice) / (GridLevels > 1 ? GridLevels - 1 : 1);
}