using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTrader.Domain.Interfaces;
using NetTrader.Domain.Options;
using System.Text;

namespace NetTrader.Infrastructure.Notifications;

public class TelegramService : ITelegramService
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly ILogger<TelegramService> _logger;

    // FIX из ревью: IConfiguration → IOptions<TelegramOptions>
    public TelegramService(HttpClient httpClient, IOptions<TelegramOptions> options, ILogger<TelegramService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var opts = options.Value;
        _botToken = opts.BotToken;
        _chatId = opts.ChatId;
    }

    public async Task SendMessageAsync(string message)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new { chat_id = _chatId, text = message, parse_mode = "Markdown" };

            var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(url, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось отправить сообщение в Telegram");
        }
    }
}
