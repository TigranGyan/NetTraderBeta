namespace NetTrader.Domain.Constants;

/// <summary>
/// FIX #6, #7: Единый источник истины для разрешённых монет и корреляционных групп.
/// 
/// Ранее AllowedTopCoins дублировался в GeminiAdvisor и BinanceFuturesClient,
/// а CoinGroups — в GeminiAdvisor и GridSettingsListValidator.
/// Добавление монеты в одно место без другого = невидимый баг.
/// </summary>
public static class SymbolConfig
{
    public static readonly HashSet<string> AllowedSymbols = new()
    {
        "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "AVAXUSDT", "ADAUSDT",
        "ARBUSDT", "OPUSDT", "LINKUSDT", "AAVEUSDT", "UNIUSDT",
        "DOGEUSDT", "SHIBUSDT", "FTMUSDT", "WLDUSDT",
        "PEPEUSDT", "FLOKIUSDT", "AXSUSDT", "XRPUSDT",
    };

    public static readonly Dictionary<string, string> CoinGroups = new()
    {
        { "ETHUSDT", "Layer1" }, { "SOLUSDT", "Layer1" }, { "AVAXUSDT", "Layer1" }, { "ADAUSDT", "Layer1" },
        { "ARBUSDT", "Layer2" }, { "OPUSDT", "Layer2" },
        { "LINKUSDT", "DeFi" }, { "AAVEUSDT", "DeFi" }, { "UNIUSDT", "DeFi" },
        { "DOGEUSDT", "Meme" }, { "SHIBUSDT", "Meme" }, { "PEPEUSDT", "Meme" }, { "FLOKIUSDT", "Meme" },
        { "BNBUSDT", "Exchange" }, { "FTMUSDT", "Exchange" },
        { "WLDUSDT", "AI" }, { "AXSUSDT", "Gaming" },
        { "BTCUSDT", "Bitcoin" }, { "XRPUSDT", "Payment" },
    };
}