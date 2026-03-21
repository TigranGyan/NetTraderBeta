namespace NetTrader.Domain.Constants;

/// <summary>
/// FIX #11: Все статусы сессий — в одном месте.
/// Ранее магические строки ("Active", "Trailing" и т.д.) были разбросаны по коду.
/// Опечатка в строке → сломанный фильтр в БД → потерянные позиции.
/// </summary>
public static class TradeStatus
{
    public const string Active = "Active";
    public const string ActiveMarket = "ActiveMarket";
    public const string Trailing = "Trailing";
    public const string Closed = "Closed";
    public const string ClosedByAI = "ClosedByAI";
    public const string ClosedManually = "ClosedManually";
    public const string EmergencyStopped = "EmergencyStopped";
    public const string Pending = "Pending"; // FIX #9: для идемпотентной записи сессий

    /// <summary>Все "живые" статусы — для фильтров в репозитории.</summary>
    public static readonly string[] AllActive = { Active, ActiveMarket, Trailing, Pending };
}