# NetTrader: Technical Deep Dive

## 1. Trading Cycle (TradingBotWorker)

The bot runs as a `BackgroundService` with a triple-loop architecture:

```
while (!cancellationToken.IsCancellationRequested)
{
    ProcessCommands()           // instant
    MonitorAndProtect()         // every 10s — ROI, trailing
    MlQuickScan()              // every 15s — ML scan, trigger AI if signal
    ScheduledAiCycle()         // every 30m — or triggered by ML/manual
    Sleep(1s)
}
```

**ML Quick Scan** (new in v2.1):
- Runs every 15 seconds between AI cycles
- Fetches top volatile pairs from WebSocket cache
- Computes indicators + runs ML predictions locally
- If strong signal found (GAP >= 0.08, P >= 0.44) → triggers AI cycle immediately
- 5-minute cooldown prevents Gemini API spam (MinAiCooldownSeconds=300)
- Logs: `ML Quick Scan: 2 strong signals found → AI cycle!`

---

## 2. Indicator Enrichment (IndicatorEnrichmentService)

Uses `Skender.Stock.Indicators`. Calculations on **30m / 1h / 2h / 4h** timeframes:

| Indicator | Params | Use |
|-----------|--------|-----|
| RSI | 14 | Overbought/oversold |
| ADX | 14 | Trend strength (>25 = trending) |
| EMA Trend | 50 | `(close - EMA50) / EMA50 * 100%` |
| MACD | 12, 26, 9 | Impulse direction (histogram sign) |
| ATR | 14 | Volatility in points |
| Bollinger Bands | 20, 2 sigma | Squeeze detection |
| Keltner Channels | 20, 1.5 ATR | Squeeze reference |

**Bollinger Squeeze**: `BB.Upper < KC.Upper && BB.Lower > KC.Lower` → `IsSqueezing = true`.
Signal: abnormally low volatility, breakout expected.

---

## 3. ML Layer (MLSignalService v2.1)

### Architecture: Two Binary Classifiers

Two independent FastTree+Platt models trained on the same features:

```
ModelLong.zip  → P(price hits +3% before -2% within 12h)
ModelShort.zip → P(price hits -3% before +2% within 12h)
```

### Training Data (NettraderML-Data)

**LLMData** collects:
- 7 symbols: BTCUSDT, ETHUSDT, SOLUSDT, XRPUSDT, DOGEUSDT, BNBUSDT, AVAXUSDT
- 6 years of 30m candles (2020 → today) + 1h/2h for multi-timeframe features
- BTC 1h candles for market context (BtcTrend, BtcRsi)
- **738,144 total rows**

**Label Engine (Trailing Stop simulation):**
For each 30m candle, look forward 24 candles (12 hours):
- Price hits `close * 1.03` (TP +3%) before `close * 0.98` (SL -2%) → **LONG**
- Price hits `close * 0.97` (TP -3%) before `close * 1.02` (SL +2%) → **SHORT**
- Neither → **HOLD**

### 24 Features

| # | Feature | Formula |
|---|---------|---------|
| 1 | `Adx_30m` | ADX(14) on 30m candles |
| 2 | `Volatility24h` | (maxHigh - minLow) / close * 100 over 48 candles |
| 3 | `Trend_30m` | (close[i] - close[i-10]) / close[i-10] * 100 |
| 4 | `Trend_1h` | Same on 1h candles |
| 5 | `Trend_2h` | Same on 2h candles |
| 6 | `Rsi_30m` | RSI(14) on 30m |
| 7 | `StochRsi_30m` | StochRSI(14,14,3,1) on 30m |
| 8 | `Rsi_1h` | RSI(14) on 1h |
| 9 | `Rsi_2h` | RSI(14) on 2h |
| 10 | `PositionInRange24h` | (close - min48) / (max48 - min48) in [0, 1] |
| 11 | `MacdNorm_30m` | MACD(12,26,9) histogram / price * 10000 (cross-symbol normalized) |
| 12 | `AtrPercent_30m` | ATR(14) / close * 100 |
| 13 | `BBandsWidth_30m` | Bollinger Bands width |
| 14 | `VolumeSpike` | Current volume / SMA(20 volume) |
| 15 | `VwapDist_30m` | (close - VWAP) / VWAP * 100 |
| 16 | `ConsecCandles_30m` | Count of consecutive same-direction candles (+/-) |
| 17 | `DistEma50_30m` | (close - EMA50) / EMA50 * 100 |
| 18 | `DistEma200_30m` | (close - EMA200) / EMA200 * 100 |
| 19 | `BtcTrend_1h` | BTC 1h trend (market leader context) |
| 20 | `BtcRsi_1h` | BTC RSI(14) on 1h |
| 21 | `BodyPct_30m` | |body| / (high - low) — candle body ratio |
| 22 | `UpperWickPct_30m` | Upper wick / (high - low) — rejection signal |
| 23 | `LowerWickPct_30m` | Lower wick / (high - low) — buying pressure signal |
| 24 | `HourOfDay` | UTC hour 0-23 (Asia/Europe/US session patterns) |

### Model Configuration

```
Algorithm: FastTree (gradient boosted trees) + Platt calibration
Trees: 300
Leaves per tree: 30
Min samples per leaf: 30
Learning rate: 0.05
Weights: LONG=1.5x, SHORT=1.5x, HOLD=1.0x
Split: Temporal (first 80% train, last 20% test — no future data leakage)
```

### Decision Logic

```
Threshold = 0.44
MinGap = 0.08

float gap = |P(LONG) - P(SHORT)|
if (gap < MinGap) return "HOLD"        // Both models uncertain
if (P(LONG) > P(SHORT) && P(LONG) > Threshold) return "LONG"
if (P(SHORT) > P(LONG) && P(SHORT) > Threshold) return "SHORT"
return "HOLD"
```

### Production Metrics (OOS — Out of Sample)

| Metric | ModelLong | ModelShort |
|--------|-----------|------------|
| AUC-ROC | 71.45% | 70.83% |
| Precision (at 0.44/0.08) | 44.8% | 44.2% |
| Avg Precision | **44.5%** | |

**Expected Value calculation** (SL -2%, TP +5%, R:R = 1:2.5):
- Breakeven precision: 28.5%
- Actual precision: 44.5%
- EV per 100 trades (50 long + 50 short, $100 risk each):
  - Wins: 44.5 * $250 = +$11,125
  - Losses: 55.5 * $100 = -$5,550
  - **Net: +$5,575 (+55.75$ per trade)**

### Integration with GeminiAdvisor

Each coin in the AI prompt gets ML context:
```
SOLUSDT | RSI=58 | ADX=32 | Trend=+1.2 | ML:LONG P(L)=0.48 P(S)=0.39 GAP=0.09
ETHUSDT | RSI=44 | ADX=18 | Trend=-0.8 | ML:HOLD P(L)=0.45 P(S)=0.47 GAP=0.02
```

Rules (v9 — ML as information):
- ML signal is **INFORMATION** for AI, not a blocker
- AI can open LONG even if ML says SHORT (if AI has macro/news reason)
- AI can trade on ML:HOLD if other signals are strong
- ML gap and probabilities influence AI's confidence, but don't block

---

## 4. Position Management (GridTradingManager)

### Margin Check (v2.1 — auto-reduce)

```csharp
// Protect against negative available balance (unrealized PnL loss)
avail = Math.Max(0, GetAvailableBalanceAsync());

// TotalInvestment is nominal. Actual margin = nominal / leverage
marginNeeded = TotalInvestment / leverage;

if (marginNeeded > available)
{
    maxNominal = available * leverage;
    if (maxNominal >= 5$)
        TotalInvestment = maxNominal;  // auto-shrink to fit
    else
        skip;  // truly not enough
}
```

### Direction Flip + Duplicate Guard

When AI recommends a position for a symbol that already has one:

**Same direction** (e.g., existing LONG, AI says LONG):
- Only update SL/TP, don't open new position
- Prevents double-opening and "insufficient margin" errors

**Opposite direction** (e.g., existing LONG, AI says SHORT):
1. Cancel all SL/TP/limit orders for the symbol
2. Close existing position via `ClosePositionAsync`
3. Mark old session as `Closed` with `ClosedAt`
4. Open new position in requested direction
5. Set new SL/TP

### Stale Session Cleanup

During MonitorAndProtect, if a session is Active/ActiveMarket but `posAmt == 0` on exchange
(SL/TP hit on exchange side), the session is auto-closed:
- Status → `Closed`, ClosedAt → now
- Prevents ghost sessions that never get cleaned up

### Execution Order

MKT orders execute before GRID orders (`OrderByDescending(g => g.OrderType)`):
- MKT (OrderType=1,2) → fast execution, locks less margin
- GRID (OrderType=0) → many limit orders, locks more margin
- Prevents margin exhaustion from grid orders blocking market entries

### Trailing Stop Algorithm (Leverage-Adjusted)

When ROI > `TrailingActivationRoiPercent` (3%):
1. Cancel all grid/limit orders
2. Calculate callback: `Rate = TrailingCallbackRatePercent / Leverage` = 3.0 / 10 = **0.30%**
3. Execute:
   - **Binance**: `TRAILING_STOP_MARKET` with % callback
   - **Bybit**: Convert to Price Distance (`Price * Rate`)
4. Position closes only when price reverses by 0.30% from peak

---

## 5. AI Advisor (GeminiAdvisor v9.1)

### AI-Driven Decision Making

| Aspect | Old (v8) | New (v9.1) |
|--------|----------|------------|
| Position size | Hardcoded $15-30 | AI decides, system guards (min $5, max 25% BP per position) |
| SL/TP | Always overridden by ATR formula | **Auto-calculated from actual entry price** (-2%/+5%) |
| Grid levels | Always overridden | AI sets, system only enforces 5-30 range |
| FGI limits | None | FGI<25 or >75: max 2 positions, 15% BP each |
| ML weight | "Information only" | **Strong guidance**: P(SHORT)>P(LONG) → don't LONG |

### SL/TP from Actual Entry Price (v9.1)

After market order fills, system gets `EntryPrice` from exchange and recalculates:
```
LONG:  SL = entryPrice × 0.98 (exactly -2%)
       TP = entryPrice × 1.05 (exactly +5%)
SHORT: SL = entryPrice × 1.02 (exactly +2%)
       TP = entryPrice × 0.95 (exactly -5%)
```
This ensures SL/TP is always exact relative to fill price, not the price at prompt-build time.

### FGI-Based Position Limits (v9.1)

| FGI Range | Max Positions | Max Size per Position | Bias |
|-----------|--------------|----------------------|------|
| 0-25 (Extreme Fear) | 2 | 15% buying power | Cautious, prefer SHORT |
| 26-40 (Fear) | 3 | 20% buying power | Normal |
| 41-74 (Normal) | 5 | 25% buying power | Follow ML/technicals |
| 75-100 (Extreme Greed) | 2 | 15% buying power | Prefer SHORT |

### Investment validation
- AI gave < $5? → use CalcSmartInvestment() fallback
- AI gave > 25% buying power? → cap
- Otherwise: **trust AI's amount**

### CalcSmartInvestment (fallback sizing)

```csharp
basePercent = 0.05 (5% of buying power)
fgiMultiplier = FGI < 25 ? 1.3 : FGI > 75 ? 0.7 : 1.0
volMultiplier = ATR > 3% ? 0.7 : ATR < 1% ? 1.3 : 1.0
mlMultiplier = GAP > 0.15 ? 1.3 : GAP < 0.05 ? 0.7 : 1.0
investment = balance * leverage * basePercent * fgiMult * volMult * mlMult
```

---

## 6. Risk Management Summary

| Protection | Parameter | Default | Action |
|-----------|-----------|---------|--------|
| ML Signal | Threshold=0.44, MinGap=0.08 | — | Info for AI (not blocking) |
| Margin Guard | — | — | Auto-reduce or skip |
| Stop Loss | AI-set | ~-2% | Exchange order |
| Take Profit | AI-set | ~+5% | Exchange order |
| Trailing Stop | TrailingActivationRoiPercent | 3.0% | 0.3% callback |
| Duplicate Guard | — | — | Update SL/TP only |
| Daily Drawdown | DailyDrawdownPausePercent | 10% | Pause 12h |
| Balance Guard | EmergencyStopBalancePercent | 50% | Emergency stop |
| `/panic` | — | — | Close all immediately |

---

## 7. Retraining ML Models

Models should be retrained every **1-3 months** or on major market regime change:

```bash
# 1. Fetch data (7 symbols x 6 years, ~20 min)
cd NettraderML-Data/LLMData && dotnet run

# 2. Train (FastTree + Platt, ~2 min)
cd ../NettraderML && dotnet run

# 3. Deploy to bot
cp output/ModelLong.zip  ../../NetTrader/NetTrader.Worker/mlmodel/
cp output/ModelShort.zip ../../NetTrader/NetTrader.Worker/mlmodel/
git add -A && git commit && git push  # CI/CD deploys automatically
```

**Target metrics:**
- AUC-ROC > 70%
- Precision > 42% at threshold 0.44 / MinGap 0.08
- Both L-Prec and S-Prec balanced (within 5% of each other)

**Key improvements in v2.1:**
- FastForest → **FastTree** (gradient boosting, better accuracy)
- Added **Platt calibration** (properly calibrated probabilities)
- MACD normalized by price (**MacdNorm** — cross-symbol comparable)
- Added **HourOfDay** feature (crypto session patterns)
- 11 features → **24 features** (VWAP, BBands, EMA200, candle anatomy, BTC context)
- Weight 1.5x (balanced, not too aggressive)
- Temporal train/test split (no future data leakage)

---

## 8. Infrastructure

### Docker / GCP

```dockerfile
ENV DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2SUPPORT=0  # Force HTTP/1.1 (GCP fix)
```

### Binance API Sync

```csharp
AutoTimestamp = true
TimestampRecalculationInterval = 30 seconds
GetServerTimeAsync() on startup  // Force calibration before first API call
```

### Telegram Commands

| Command | Action |
|---------|--------|
| `/panic` | Close all positions on all exchanges |
| `/pause` | Pause trading |
| `/resume` | Resume trading |
| `/leverage N` | Set leverage (e.g., `/leverage 10`) |
| `/force` | Force immediate AI cycle |
| `/status` | Show current positions and balance |
