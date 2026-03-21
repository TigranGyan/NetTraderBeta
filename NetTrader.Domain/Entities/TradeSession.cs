namespace NetTrader.Domain.Entities;

public class TradeSession
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public decimal LowerPrice { get; set; }
    public decimal UpperPrice { get; set; }
    public string Status { get; set; } = "Active"; // Статусы: Active, Closed, Stopped, Trailing

    // FIX: Добавляем TotalInvestment чтобы корректно считать ROI для трейлинга
    public decimal TotalInvestment { get; set; } = 20m;

    /// <summary>Stop loss price for this grid (for repair / ensure SL-TP).</summary>
    public decimal StopLoss { get; set; }
    /// <summary>Take profit price for this grid (for repair / ensure SL-TP).</summary>
    public decimal TakeProfit { get; set; }
}