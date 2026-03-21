namespace NetTrader.Domain.Entities;

public class GridOrder
{
	public decimal Price { get; set; }
	public decimal Quantity { get; set; }
	public string Side { get; set; } = string.Empty; // "BUY" или "SELL"
}