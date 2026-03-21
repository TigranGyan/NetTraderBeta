namespace NetTrader.Domain.Entities;

public class MarketData
{
    public string Symbol { get; set; } = string.Empty;
    public decimal CurrentPrice { get; set; }
    public decimal Volatility24h { get; set; }

    public decimal TickSize { get; set; }
    public decimal StepSize { get; set; }
    public decimal MinNotional { get; set; }

    public List<Candle> History { get; set; } = new();

    // ═══ 24h High/Low — РЕАЛЬНЫЕ границы текущего рэнджа ═══
    public decimal High24h { get; set; }
    public decimal Low24h { get; set; }
    /// <summary>24h range в процентах = (High - Low) / Low * 100</summary>
    public decimal Range24hPercent => Low24h > 0 ? (High24h - Low24h) / Low24h * 100m : 0m;

    // ═══ ADX (4h) — сила тренда ═══
    /// <summary>
    /// ADX < 20: слабый тренд / БОКОВИК → грид хорошо работает
    /// ADX 20-25: средний тренд → осторожно
    /// ADX > 25: сильный тренд → грид рискованно
    /// </summary>
    public decimal Adx { get; set; }

    // ═══ 4h индикаторы (долгосрочные) ═══
    public decimal Rsi { get; set; }
    public decimal Ema200 { get; set; }
    public decimal Atr { get; set; }
    public decimal MacdHistogram { get; set; }
    public decimal MacdLine { get; set; }
    public decimal MacdSignal { get; set; }
    public decimal BbUpper { get; set; }
    public decimal BbMiddle { get; set; }
    public decimal BbLower { get; set; }
    public decimal BbWidth => BbMiddle > 0 ? (BbUpper - BbLower) / BbMiddle * 100m : 0m;
    public decimal VolumeRatio { get; set; }
    public decimal FundingRate { get; set; }

    // ═══ 1h индикаторы (краткосрочные — для тайминга) ═══
    public decimal Rsi1h { get; set; }
    public decimal MacdHist1h { get; set; }
    public decimal BbUpper1h { get; set; }
    public decimal BbLower1h { get; set; }
    /// <summary>Позиция цены в 1h BB (0=на нижней, 1=на верхней)</summary>
    public decimal BbPosition1h => (BbUpper1h - BbLower1h) > 0
        ? (CurrentPrice - BbLower1h) / (BbUpper1h - BbLower1h)
        : 0.5m;

    // ═══ МАКРО ═══
    public int? FearGreedIndex { get; set; }
    public DateTime? FOMCDate { get; set; }
    public decimal? BTCDominance { get; set; }
}