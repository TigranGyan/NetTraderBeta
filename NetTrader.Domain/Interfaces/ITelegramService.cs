namespace NetTrader.Domain.Interfaces;

public interface ITelegramService
{
	Task SendMessageAsync(string message);
}