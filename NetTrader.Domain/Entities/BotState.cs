using System.Threading.Channels;

namespace NetTrader.Domain.Entities;

/// <summary>
/// Потокобезопасное состояние бота.
/// 
/// FIX из ревью: Заменены volatile-поля на Interlocked-операции
/// для гарантии атомарности. Добавлен Channel&lt;BotCommand&gt;
/// для безопасной передачи команд между TelegramListenerWorker и TradingBotWorker.
/// </summary>
public sealed class BotState
{
    // ═══ Потокобезопасные флаги через Interlocked ═══
    private int _isPaused;
    private int _emergencyStopRequested;
    private int _leverage;

    /// <summary>
    /// Канал команд от Telegram к торговому движку.
    /// UnboundedChannel — не блокирует отправителя, читатель обрабатывает по мере поступления.
    /// </summary>
    public Channel<BotCommand> CommandChannel { get; }

    public BotState(int defaultLeverage = 10)
    {
        _leverage = defaultLeverage;
        CommandChannel = Channel.CreateUnbounded<BotCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,  // Только TradingBotWorker читает
            SingleWriter = false  // Telegram + потенциально другие источники
        });
    }

    public bool IsPaused
    {
        get => Interlocked.CompareExchange(ref _isPaused, 0, 0) == 1;
        set => Interlocked.Exchange(ref _isPaused, value ? 1 : 0);
    }

    public bool EmergencyStopRequested
    {
        get => Interlocked.CompareExchange(ref _emergencyStopRequested, 0, 0) == 1;
        set => Interlocked.Exchange(ref _emergencyStopRequested, value ? 1 : 0);
    }

    public int Leverage
    {
        get => Interlocked.CompareExchange(ref _leverage, 0, 0);
        set => Interlocked.Exchange(ref _leverage, value);
    }

    /// <summary>Отправить команду в канал (вызывается из TelegramListenerWorker)</summary>
    public ValueTask SendCommandAsync(BotCommand command, CancellationToken ct = default)
        => CommandChannel.Writer.WriteAsync(command, ct);

    /// <summary>Попытка прочитать команду без блокировки (вызывается из TradingBotWorker)</summary>
    public bool TryReadCommand(out BotCommand? command)
        => CommandChannel.Reader.TryRead(out command);
}
