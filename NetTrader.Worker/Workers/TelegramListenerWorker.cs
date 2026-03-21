using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTrader.Domain.Constants;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Interfaces;
using NetTrader.Domain.Options;

namespace NetTrader.Worker.Workers;

public class TelegramListenerWorker : BackgroundService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramListenerWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly BotState _botState;
    private readonly string _botToken;
    private readonly string _chatId;
    private long _lastUpdateId = 0;

    public TelegramListenerWorker(
        ILogger<TelegramListenerWorker> logger,
        IOptions<TelegramOptions> telegramOptions,
        IServiceProvider serviceProvider,
        BotState botState,
        IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        _serviceProvider = serviceProvider;
        _botState = botState;

        var opts = telegramOptions.Value;
        _botToken = opts.BotToken;
        _chatId = opts.ChatId;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_botToken)) return;
        _logger.LogInformation("📡 Telegram Command Listener запущен. Жду команд...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{_botToken}/getUpdates?offset={_lastUpdateId + 1}&timeout=5";
                var response = await _httpClient.GetAsync(url, stoppingToken);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(stoppingToken);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("result", out var results) && results.GetArrayLength() > 0)
                    {
                        foreach (var update in results.EnumerateArray())
                        {
                            _lastUpdateId = update.GetProperty("update_id").GetInt64();

                            if (update.TryGetProperty("message", out var message) &&
                                message.TryGetProperty("text", out var textElement) &&
                                message.TryGetProperty("chat", out var chat) &&
                                chat.TryGetProperty("id", out var chatIdElement))
                            {
                                string chatId = chatIdElement.GetInt64().ToString();
                                string text = textElement.GetString()?.Trim() ?? "";

                                // FIX #13: Проверяем И chat.id, И from.id для дополнительной безопасности.
                                // Ранее проверялся только chatId — этого достаточно для приватных чатов,
                                // но в группах любой участник мог отправить команду.
                                if (chatId != _chatId)
                                {
                                    _logger.LogWarning("⚠️ Команда из неизвестного чата: {ChatId}", chatId);
                                    continue;
                                }

                                // Дополнительная проверка: если from.id доступен, сверяем с chatId
                                // (для приватных чатов from.id == chatId)
                                if (message.TryGetProperty("from", out var from) &&
                                    from.TryGetProperty("id", out var fromIdElement))
                                {
                                    string fromId = fromIdElement.GetInt64().ToString();
                                    if (fromId != _chatId)
                                    {
                                        _logger.LogWarning("⚠️ Команда от неавторизованного пользователя: from={FromId}, chat={ChatId}",
                                            fromId, chatId);
                                        continue;
                                    }
                                }

                                await ProcessCommandAsync(text);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // Graceful shutdown
            }
            catch (Exception ex) { _logger.LogWarning("Сетевая ошибка при опросе Telegram: {Message}", ex.StackTrace); }

            await Task.Delay(2000, stoppingToken);
        }
    }

    private async Task ProcessCommandAsync(string text)
    {
        using var scope = _serviceProvider.CreateScope();
        var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramService>();
        var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
        var executor = scope.ServiceProvider.GetRequiredService<IOrderExecutor>();

        try
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            string command = parts[0].ToLower();
            string arg = parts.Length > 1 ? parts[1].ToUpper() : "";

            switch (command)
            {
                case "/status":
                    decimal balance = await executor.GetMarginBalanceAsync();
                    var activeSessions = await tradeRepo.GetActiveSessionsAsync();
                    string state = _botState.IsPaused ? "⏸ ПАУЗЕ (Остановлен)" : "▶️ РАБОТЕ (Сканирует рынок)";

                    string gridsList = activeSessions.Any()
                        ? string.Join("\n", activeSessions.Select(s => $"🔹 {s.Symbol}"))
                        : "Нет активных сеток";

                    string msg = $"🤖 *СТАТУС БОТА*\n\nРежим: Бот в {state}\n💰 Баланс: {balance:F2}$\n🕸 Активных сеток: {activeSessions.Count}\n⚙️ Плечо: x{_botState.Leverage}\n\n*В торгах сейчас:*\n{gridsList}";
                    await telegramService.SendMessageAsync(msg);
                    break;

                case "/close":
                    if (string.IsNullOrEmpty(arg))
                    {
                        await telegramService.SendMessageAsync("⚠️ Укажи монету. Пример: `/close BTCUSDT`");
                        break;
                    }

                    var sessionToClose = (await tradeRepo.GetActiveSessionsAsync()).FirstOrDefault(s => s.Symbol == arg);
                    if (sessionToClose == null)
                    {
                        await telegramService.SendMessageAsync($"⚠️ Сетка *{arg}* не найдена.");
                        break;
                    }

                    await telegramService.SendMessageAsync($"⏳ Закрываю *{arg}*...");
                    await executor.CancelAllGridOrdersAsync(arg);
                    await executor.ClosePositionAsync(arg);

                    sessionToClose.Status = TradeStatus.ClosedManually; // FIX #11
                    sessionToClose.ClosedAt = DateTime.UtcNow;
                    await tradeRepo.UpdateSessionAsync(sessionToClose);

                    await telegramService.SendMessageAsync($"✅ Сетка *{arg}* успешно закрыта!");
                    break;

                case "/leverage":
                    if (string.IsNullOrEmpty(arg) || !int.TryParse(arg, out int newLev) || newLev < 1 || newLev > 125)
                    {
                        await telegramService.SendMessageAsync("⚠️ Укажи корректное плечо (от 1 до 125).\nПример: `/leverage 20`");
                        break;
                    }

                    await _botState.SendCommandAsync(new BotCommand
                    {
                        Type = BotCommandType.SetLeverage,
                        Argument = newLev.ToString()
                    });
                    _botState.Leverage = newLev;
                    await telegramService.SendMessageAsync($"⚙️ Плечо успешно изменено на *x{newLev}*!\nВсе новые сетки будут открываться с этим плечом.");
                    break;

                case "/pause":
                    await _botState.SendCommandAsync(new BotCommand { Type = BotCommandType.Pause });
                    _botState.IsPaused = true;
                    await telegramService.SendMessageAsync("⏸ *Бот поставлен на паузу.*\nНовые сделки не открываются.");
                    break;

                case "/resume":
                    await _botState.SendCommandAsync(new BotCommand { Type = BotCommandType.Resume });
                    _botState.IsPaused = false;
                    await telegramService.SendMessageAsync("▶️ *Бот снят с паузы.*\nВозвращаюсь к работе.");
                    break;

                case "/panic":
                    await _botState.SendCommandAsync(new BotCommand { Type = BotCommandType.Panic });
                    _botState.EmergencyStopRequested = true;
                    await telegramService.SendMessageAsync("🚨 *ПАНИКА!*\nНачинаю экстренное закрытие ВСЕХ позиций...");
                    break;

                case "/help":
                case "/start":
                    string helpMsg = "🕹 *ПУЛЬТ УПРАВЛЕНИЯ*\n\n" +
                     "`/status` — Баланс и открытые монеты\n" +
                     "`/close BTCUSDT` — Закрыть одну сетку\n" +
                     "`/pause` — Остановить поиск новых сделок\n" +
                     "`/resume` — Продолжить работу\n" +
                     "`/panic` — 🚨 Закрыть всё\n" +
                     "`/leverage 20` — Изменить кредитное плечо для новых сеток";
                    await telegramService.SendMessageAsync(helpMsg);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка команды: {Command}", text);
        }
    }
}