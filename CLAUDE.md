# NetTrader — AI Context Guide

> This file helps AI assistants (Claude, Gemini, etc.) quickly understand the entire project.
> Read this FIRST before modifying any code.

## What is NetTrader?

Autonomous cryptocurrency **futures trading bot** on .NET 9. Trades on **Binance Futures** (Bybit support exists but disabled). Uses three layers of analysis:
1. **ML models** (FastTree + Platt) scan every 15 seconds for entry signals
2. **Gemini AI** (Google) decides direction, size, SL/TP based on macro + technicals
3. **Trailing Stop** locks profits when ROI exceeds 3%

## Project Structure (Clean Architecture)

```
NetTrader.Domain/          — Entities, interfaces, options, constants. ZERO dependencies.
  ├── Entities/            — TradeSession, MarketData, GridSettings, BotState, GridOrder
  ├── Interfaces/          — IOrderExecutor, IAiAdvisor, IMLSignalService, ITradeRepository
  ├── Options/             — TradingOptions, BinanceOptions, BybitOptions
  ├── Constants/           — TradeStatus (7 states), SymbolConfig (7 coins), BotCommandType
  └── Validation/          — GridSettingsValidator (FluentValidation)

NetTrader.Application/     — Business logic
  ├── Services/
  │   ├── GridTradingManager.cs    — ⭐ CORE: position lifecycle, margin, SL/TP, trailing
  │   └── IndicatorEnrichmentService.cs — RSI, ADX, EMA, MACD, ATR, Bollinger, Squeeze
  └── Calculations/
      └── GridMathCalculator.cs    — Grid order math

NetTrader.Infrastructure/  — External integrations
  ├── Exchanges/
  │   ├── BinanceFuturesClient.cs  — REST + WebSocket, auto-timestamp sync
  │   └── BybitClient.cs           — V5 Perpetual (disabled)
  ├── AiServices/
  │   └── GeminiAdvisor.cs         — ⭐ AI prompt v9.1, 3-tier fallback, JSON parsing
  ├── MLServices/
  │   └── MLSignalService.cs       — 24 features, dual FastTree models
  ├── MacroServices/
  │   └── MacroAnalyzer.cs         — Fear & Greed, BTC Dominance, FOMC dates
  ├── Repositories/
  │   └── TradeRepository.cs       — PostgreSQL via EF Core
  └── DependencyInjection.cs       — IoC registration

NetTrader.Worker/          — Entry point (BackgroundService)
  ├── Workers/
  │   ├── TradingBotWorker.cs      — ⭐ Main triple-loop: commands/monitor/ML/AI
  │   └── TelegramListenerWorker.cs — 12 Telegram commands
  ├── Program.cs
  └── appsettings.json

NettraderML-Data/          — ML training pipeline (separate solution)
  ├── LLMData/Program.cs           — Fetch 6 years of klines, compute 24 features + labels
  └── NettraderML/Program.cs       — Train FastTree + Platt, save ModelLong.zip / ModelShort.zip
```

## Triple-Loop Architecture

```
while (!cancelled)
{
    // EVERY 1s — Instant commands
    if (EmergencyStopRequested) → close ALL positions NOW
    Process Telegram commands (pause/resume/leverage/cycle)

    // EVERY 10s — Fast monitor
    For each active session:
      - Get real PnL from exchange REST API
      - If posAmt == 0 → position closed by SL/TP on exchange, clean up session
      - If ROI > 3% → activate TrailingStop (callback = 3.0% / leverage = 0.3% at 10x)
      - Ensure SL/TP orders exist on exchange

    // EVERY 15s — ML Quick Scan
    Fetch top 30 volatile pairs from WebSocket cache
    Compute 24 ML features for each (RSI, ADX, trends, MACD, BTC context...)
    Run ModelLong + ModelShort predictions
    If GAP >= 0.08 AND P >= 0.44 → trigger AI cycle immediately!
    Cooldown: 5 minutes minimum between AI cycles

    // EVERY 30m — Scheduled AI Cycle (or triggered by ML/manual)
    1. MacroAnalyzer → FGI, BTC.D, FOMC distance
    2. IndicatorEnrichment → RSI/ADX/EMA/MACD/ATR/Squeeze on 30m/1h/2h/4h
    3. MLSignalService → 24 features → P(LONG) + P(SHORT) per coin
    4. GeminiAdvisor → AI strategy (prompt v9.1, Gemini 3.1 Pro + Search)
    5. GridTradingManager → validate, margin check, execute orders

    Sleep(1s)
}
```

## Key Trading Logic

### SL/TP (Stop Loss / Take Profit)
**Recalculated from ACTUAL entry price** after market order fills:
- LONG: SL = entry × 0.98 (-2%), TP = entry × 1.05 (+5%) → R:R = 1:2.5
- SHORT: SL = entry × 1.02 (+2%), TP = entry × 0.95 (-5%) → R:R = 1:2.5
- AI provides approximate SL/TP, system recalculates from real fill price

### Trailing Stop
- Activates when ROI > `TrailingActivationRoiPercent` (3%)
- Callback = `TrailingCallbackRatePercent / Leverage` = 3.0 / 10 = **0.30%**
- Uses Binance `TRAILING_STOP_MARKET` conditional order

### Direction Flip
If AI changes direction (LONG→SHORT or SHORT→LONG):
1. Cancel all SL/TP/limit orders
2. Close existing position
3. Mark session as Closed
4. Open new position in opposite direction

### Margin Management
```
available = max(0, GetAvailableBalance())  // protect negative
marginNeeded = TotalInvestment / leverage
if marginNeeded > available:
    maxNominal = available * leverage
    if maxNominal >= 5$ → auto-shrink investment
    else → skip (not enough margin)
```

### Execution Order
MKT orders (OrderType=1,2) execute BEFORE GRID orders (OrderType=0).
Prevents grids from locking all margin before market entries.

## ML Model (v2.1)

### Architecture
Two independent binary classifiers (FastTree + Platt calibration):
- `ModelLong.zip` → P(price hits +3% before -2% within 12h)
- `ModelShort.zip` → P(price hits -3% before +2% within 12h)

### 24 Features
| # | Feature | Formula |
|---|---------|---------|
| 1 | Adx_30m | ADX(14) trend strength |
| 2 | Volatility24h | (maxHigh-minLow)/close*100 over 48 candles |
| 3-5 | Trend_30m/1h/2h | (close-close[10])/close[10]*100 |
| 6-9 | Rsi_30m, StochRsi_30m, Rsi_1h, Rsi_2h | RSI(14), StochRSI(14,14,3,1) |
| 10 | PositionInRange24h | (close-min)/(max-min) in [0,1] |
| 11 | MacdNorm_30m | MACD(12,26,9) / price * 10000 |
| 12 | AtrPercent_30m | ATR(14) / close * 100 |
| 13 | BBandsWidth_30m | Bollinger Bands width |
| 14 | VolumeSpike | volume / SMA(20 volume) |
| 15 | VwapDist_30m | (close-VWAP)/VWAP*100 |
| 16 | ConsecCandles_30m | Count consecutive same-direction candles |
| 17-18 | DistEma50/200_30m | (close-EMA)/EMA*100 |
| 19-20 | BtcTrend_1h, BtcRsi_1h | BTC market leader context |
| 21-23 | BodyPct/UpperWick/LowerWick | Candle anatomy ratios |
| 24 | HourOfDay | UTC hour 0-23 |

### Decision Logic
```
Threshold = 0.44, MinGap = 0.08
GAP = |P(LONG) - P(SHORT)|
GAP < 0.08           → HOLD (both models uncertain)
P(LONG) > P(SHORT)   → LONG (if P(LONG) > 0.44)
P(SHORT) > P(LONG)   → SHORT (if P(SHORT) > 0.44)
```

### Metrics (Out of Sample)
- AUC-ROC: Long=71.45%, Short=70.83%
- Precision at 0.44/0.08: Long=44.8%, Short=44.2%
- Breakeven precision: 28.5% → **+55$ EV per 100$ risk**

## AI Prompt (GeminiAdvisor v9.1)

### Key Rules in Prompt
- ML signal is **strong guidance** (not a hard block):
  - P(SHORT) > P(LONG) by 3%+: do NOT go LONG unless strong reason
  - ML=HOLD: reduce size 50% or skip
- **FGI < 25**: max 2 positions, 15% buying power each (not 4 LONGs!)
- **FGI > 75**: max 2 positions, prefer SHORT
- Position sizing: max 25% buying power per position, total < 80%
- SL/TP: system auto-calculates from entry (-2%/+5%), AI provides approximate values

### Fallback Chain
1. `gemini-3.1-pro-preview` (primary, 3-min timeout per step)
2. `gemini-2.5-flash` (stable fallback, fast and reliable)

## Telegram Commands (12)

| Command | Action |
|---------|--------|
| `/panic` | Close ALL positions immediately (1s check) |
| `/pause` / `/resume` | Stop/start trading |
| `/leverage N` | Set leverage (1-125) |
| `/cycle` | Force immediate AI cycle |
| `/status` | Bot state, balance, sessions |
| `/close SYMBOL` | Close specific position |
| `/positions` | All positions with live PnL, ROI%, SL/TP |
| `/pnl` | Balance, unrealized PnL, last 5 closed trades |
| `/market SYMBOL` | Price, RSI, ADX, trends, ML signal |
| `/settings` | Current bot config |
| `/help` | Command list |

## Configuration (`appsettings.json`)

| Setting | Value | Meaning |
|---------|-------|---------|
| CheckIntervalMinutes | 30 | Scheduled AI cycle interval |
| MlQuickScanIntervalSeconds | 15 | ML scan frequency |
| MinAiCooldownSeconds | 300 | 5 min between AI cycles |
| TrailingActivationRoiPercent | 3.0 | ROI% to trigger trailing |
| TrailingCallbackRatePercent | 3.0 | Callback rate (÷ leverage) |
| MaxMarginUsagePercent | 0.8 | Max 80% balance for positions |
| EmergencyStopBalancePercent | 0.5 | Stop if balance < 50% start |
| DailyDrawdownPausePercent | 10 | Pause if day loss > 10% |
| DefaultLeverage | 10 | Default leverage |

## TradeSession Status Flow

```
Pending → Active/ActiveMarket → Trailing → Closed
                ↓                              ↑
           ClosedByAI ─────────────────────────┘
           ClosedManually ─────────────────────┘
           EmergencyStopped ───────────────────┘
```

## Common Pitfalls (for AI modifying code)

1. **Never use `cand.CurrentPrice` for SL/TP** — use actual entry price from `GetEntryPriceAsync`
2. **TrailingCallbackRatePercent is divided by leverage** — 3.0% config → 0.3% actual at 10x
3. **Trailing callback format** — use `:F2` not `:P2` (P2 multiplies by 100, showing 30% instead of 0.30%)
4. **`_predictLock`** protects PredictionEngine (not thread-safe) — always use lockAcquired pattern
5. **`WakeUpToken`** has lock protection — never access CTS without `_wakeUpLock`
6. **MKT orders before GRID** — `OrderByDescending(g => g.OrderType)` ensures this
7. **Binance WebSocket gives qty but not PnL** — use `bypassCache: true` for accurate PnL
8. **GRID is temporarily disabled** — uncomment lines in `BuildGridPrompt` to re-enable
9. **ML features must match exactly** between LLMData (training) and MLSignalService (production)
10. **Session.Leverage** stores the leverage at open time — use it for ROI calc, not current BotState.Leverage
11. **LastKnownPnL** — cached when trailing activates (position may close before we can read PnL)
12. **Gemini fallback model** — must be a real model ID (check Google AI docs for current list)

## Whitelist (7 coins)

BTCUSDT (Bitcoin), ETHUSDT (Layer1), SOLUSDT (Layer1), XRPUSDT (Payment),
DOGEUSDT (Meme), BNBUSDT (Exchange), AVAXUSDT (Layer1)

Defined in `SymbolConfig.AllowedSymbols` and `SymbolConfig.CoinGroups`.

## Deployment

- Docker container on GCP VM (`nettrader-vm`)
- CI/CD: push to `main` → auto-deploy
- Secrets via GCP Secret Manager (not in appsettings.json)
- PostgreSQL for session persistence
