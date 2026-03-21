namespace NetTrader.Domain.Entities;

/// <summary>
/// Команда от Telegram-пульта к торговому движку.
/// Передается через Channel&lt;BotCommand&gt; (потокобезопасно).
/// </summary>
public class BotCommand
{
    public BotCommandType Type { get; set; }

    /// <summary>Аргумент команды (символ для /close, число для /leverage)</summary>
    public string? Argument { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum BotCommandType
{
    Pause,
    Resume,
    Panic,
    Close,        // Argument = "BTCUSDT"
    SetLeverage   // Argument = "20"
}
