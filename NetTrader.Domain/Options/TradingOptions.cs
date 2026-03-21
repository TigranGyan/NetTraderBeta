using System.ComponentModel.DataAnnotations;

namespace NetTrader.Domain.Options;

/// <summary>
/// Типизированная конфигурация для параметров торговли и риск-менеджмента.
/// ВСЕ ранее захардкоженные константы вынесены сюда.
/// Привязывается к секции "Trading" в appsettings.json.
/// </summary>
public class TradingOptions
{
    public const string SectionName = "Trading";

    // ═══ Риск-менеджмент ═══

    /// <summary>Аварийная остановка: если баланс упал ниже этого % от начального (0.5 = 50%)</summary>
    [Range(0.1, 1.0)]
    public decimal EmergencyStopBalancePercent { get; set; } = 0.5m;

    /// <summary>Дневная просадка в %: если за день потеряли больше — пауза (10 = 10%)</summary>
    [Range(1, 50)]
    public decimal DailyDrawdownPausePercent { get; set; } = 10m;

    /// <summary>Пауза после дневной просадки в часах</summary>
    [Range(1, 48)]
    public int DrawdownPauseHours { get; set; } = 12;

    /// <summary>Максимум % от баланса на все позиции (0.8 = 80%)</summary>
    [Range(0.1, 1.0)]
    public decimal MaxMarginUsagePercent { get; set; } = 0.8m;

    // ═══ Трейлинг-стоп ═══

    /// <summary>ROI в % для активации трейлинга (3.0 = +3%)</summary>
    [Range(0.5, 50.0)]
    public decimal TrailingActivationRoiPercent { get; set; } = 3.0m;

    /// <summary>Callback Rate % для Binance Trailing Stop</summary>
    [Range(0.1, 5.0)]
    public decimal TrailingCallbackRatePercent { get; set; } = 1.5m;

    /// <summary>Минимальный ROI% чтобы закрыть когда ИИ дропает пару</summary>
    [Range(-50.0, 50.0)]
    public decimal MinRoiPercentToCloseWhenAiDrops { get; set; } = 0.0m;

    // ═══ Тайминг ═══

    /// <summary>Интервал между циклами сканирования в минутах</summary>
    [Range(1, 360)]
    public int CheckIntervalMinutes { get; set; } = 30;

    /// <summary>Количество монет для анализа</summary>
    [Range(5, 50)]
    public int TopPairsLimit { get; set; } = 30;

    // ═══ Плечо (по умолчанию) ═══

    /// <summary>Начальное кредитное плечо (можно менять через /leverage)</summary>
    [Range(1, 125)]
    public int DefaultLeverage { get; set; } = 10;
}
