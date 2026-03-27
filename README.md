# NetTrader: Advanced AI Quantitative Trading Bot

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=dotnet)
![Docker](https://img.shields.io/badge/Docker-2CA5E0?style=for-the-badge&logo=docker&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-316192?style=for-the-badge&logo=postgresql&logoColor=white)
![Binance](https://img.shields.io/badge/Binance-FCD535?style=for-the-badge&logo=binance&logoColor=black)
![Bybit](https://img.shields.io/badge/Bybit-000000?style=for-the-badge&logo=bybit&logoColor=white)
![Gemini](https://img.shields.io/badge/Google_Gemini-8E75B2?style=for-the-badge&logo=google&logoColor=white)
![ML.NET](https://img.shields.io/badge/ML.NET-512BD4?style=for-the-badge&logo=dotnet)

> Autonomous futures trading bot on .NET 9. Three-layer analysis: **ML Quick Scan** (every 15s) filters entries with 24 features, **Gemini AI** analyzes macro + news, **Trailing Stop** with leverage correction locks profits. Supports **Binance** and **Bybit** concurrently.

---

## Architecture

```
Triple-Loop Architecture:
  EVERY 1s   Commands (Telegram: /panic, /pause, /leverage)
  EVERY 10s  Fast Monitor (ROI check, Trailing Stop activation)
  EVERY 15s  ML Quick Scan (24 features, GAP>8% triggers AI)
  EVERY 30m  Scheduled AI Cycle (full macro + Gemini analysis)
  COOLDOWN 5m  Between AI cycles (avoid API spam)

AI Cycle Pipeline:
┌─────────────────────────────────────────────────────────────────┐
│  1. MacroAnalyzer    → Fear & Greed, BTC Dominance, FOMC       │
│  2. GetTopVolatility → top 30 volatile pairs (WebSocket cache) │
│  3. IndicatorEnricher→ RSI/ADX/EMA/MACD/ATR/Squeeze/BBands    │
│  4. MLSignalService  → 24 features → P(LONG) + P(SHORT)       │
│  5. GeminiAdvisor    → AI strategy (v9 prompt, Google Search)  │
│  6. GridTradingMgr   → margin check, SL/TP, position mgmt     │
│  7. OrderExecutors   → Binance Futures / Bybit V5 Perpetual   │
└─────────────────────────────────────────────────────────────────┘
```

---

## ML Pipeline v2.1

**Training data:** 738K rows, 7 symbols (BTC/ETH/SOL/XRP/DOGE/BNB/AVAX), 6 years, 30m candles

**Labels (Trailing Stop simulation):**
- LONG: price hits +3% before -2% within 24 candles (12h)
- SHORT: price hits -3% before +2% within 24 candles
- HOLD: everything else

**Model:** FastTree (gradient boosting) + Platt calibration, Weight=1.5x, temporal train/test split

**24 Features:**

| # | Feature | Description |
|---|---------|-------------|
| 1 | `Adx_30m` | ADX(14) trend strength |
| 2 | `Volatility24h` | 24h price range % |
| 3-5 | `Trend_30m/1h/2h` | Momentum (10-period) on 3 timeframes |
| 6 | `Rsi_30m` | RSI(14) on 30m |
| 7 | `StochRsi_30m` | Stochastic RSI(14,14,3,1) |
| 8-9 | `Rsi_1h/2h` | RSI(14) on 1h/2h |
| 10 | `PositionInRange24h` | Price position in 24h range [0-1] |
| 11 | `MacdNorm_30m` | MACD histogram / price * 10000 (normalized) |
| 12 | `AtrPercent_30m` | ATR(14) / price * 100 |
| 13 | `BBandsWidth_30m` | Bollinger Bands width |
| 14 | `VolumeSpike` | Volume / SMA(20) ratio |
| 15 | `VwapDist_30m` | Distance from VWAP % |
| 16 | `ConsecCandles_30m` | Consecutive same-direction candles |
| 17-18 | `DistEma50/200_30m` | Distance from EMA(50/200) % |
| 19-20 | `BtcTrend_1h/BtcRsi_1h` | BTC context (market leader) |
| 21-23 | `BodyPct/UpperWick/LowerWick` | Candle anatomy ratios |
| 24 | `HourOfDay` | UTC hour (session patterns) |

**Decision logic:**
```
Threshold = 0.44, MinGap = 0.08
GAP = |P(LONG) - P(SHORT)|
GAP < 0.08           → HOLD (no confident signal)
P(LONG) > P(SHORT)   → LONG
P(SHORT) > P(LONG)   → SHORT
```

**Metrics (OOS):**
- AUC-ROC: Long=71.45%, Short=70.83%
- Precision at optimal threshold: Long=44.8%, Short=44.2%
- With R:R 1:2.5 (SL -2%, TP +5%), breakeven = 28.5% → **+55$ EV per 100$ risk**

---

## Risk Management

| Level | Trigger | Action |
|-------|---------|--------|
| ML Signal | GAP < 8% or P < 44% | HOLD — AI sees signal as info, not blocked |
| Margin Guard | Available < 0 or < 5$ | Auto-reduce or skip |
| Stop Loss | -2% from entry | Close position |
| Take Profit | +5% from entry | Close position |
| Trailing Stop | ROI > 3% | Activate trailing (callback 0.3%) |
| Daily Drawdown | Loss > 10% of day start | Pause 12 hours |
| Balance Guard | Balance < 50% of start | Emergency stop |
| `/panic` | Telegram command | Close all positions immediately |

---

## Configuration (`appsettings.json`)

```json
{
  "Trading": {
    "CheckIntervalMinutes": 30,
    "MlQuickScanIntervalSeconds": 15,
    "MinAiCooldownSeconds": 300,
    "FastMonitoringIntervalSeconds": 10,
    "TopVolatilityCount": 30,
    "DefaultLeverage": 10,
    "TrailingActivationRoiPercent": 3.0,
    "TrailingCallbackRatePercent": 3.0,
    "MaxMarginUsagePercent": 0.8,
    "EmergencyStopBalancePercent": 0.5,
    "DailyDrawdownPausePercent": 10,
    "MlModelPath": "mlmodel"
  }
}
```

---

## Layer Breakdown (Clean Architecture)

* **`NetTrader.Domain`** — entities, interfaces, options, constants. Zero external dependencies.
* **`NetTrader.Application`** — `GridTradingManager`, `IndicatorEnrichmentService`
* **`NetTrader.Infrastructure`** — `BinanceFuturesClient`, `BybitClient`, `GeminiAdvisor`, `MLSignalService`, `MacroAnalyzer`, PostgreSQL/EF Core
* **`NetTrader.Worker`** — `TradingBotWorker` (triple-loop), `TelegramListenerWorker`

---

## Retraining ML Models

```bash
# 1. Fetch data (7 symbols x 6 years, ~20 min)
cd NettraderML-Data/LLMData && dotnet run

# 2. Train models (~2 min)
cd ../NettraderML && dotnet run

# 3. Deploy
cp output/ModelLong.zip  ../../NetTrader/NetTrader.Worker/mlmodel/
cp output/ModelShort.zip ../../NetTrader/NetTrader.Worker/mlmodel/
```

Target: AUC-ROC > 70%, Precision > 42% at threshold 0.44

---

## Disclaimer

This bot is for educational purposes. Trading cryptocurrency futures involves extremely high risk. The developers are not responsible for any financial losses. Always test on small amounts first.
