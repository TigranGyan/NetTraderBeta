using System.ComponentModel.DataAnnotations;

namespace NetTrader.Domain.Options;

/// <summary>
/// Типизированная конфигурация для Telegram.
/// Привязывается к секции "Telegram" в appsettings.json.
/// </summary>
public class TelegramOptions
{
    public const string SectionName = "Telegram";

    [Required(ErrorMessage = "Telegram BotToken обязателен")]
    public string BotToken { get; set; } = string.Empty;

    [Required(ErrorMessage = "Telegram ChatId обязателен")]
    public string ChatId { get; set; } = string.Empty;
}
