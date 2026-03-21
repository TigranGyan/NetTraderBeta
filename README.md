# 📈 NetTrader: Advanced AI Quantitative Grid Bot

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=dotnet)
![Docker](https://img.shields.io/badge/Docker-2CA5E0?style=for-the-badge&logo=docker&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-316192?style=for-the-badge&logo=postgresql&logoColor=white)
![Binance](https://img.shields.io/badge/Binance-FCD535?style=for-the-badge&logo=binance&logoColor=black)
![Gemini](https://img.shields.io/badge/Google_Gemini-8E75B2?style=for-the-badge&logo=google&logoColor=white)

> **EN:** An autonomous cryptocurrency futures trading bot built on .NET 9. It leverages Gemini AI with Deep Research (Google Search integration) to analyze macroeconomics and market sentiment, selects the best pairs, and executes grid strategies. Features hardware Trailing Stop-Loss for profit maximization and multi-level risk management.
> **RU:** Автономный торговый бот для фьючерсов на базе .NET 9. Использует Gemini AI с функцией Deep Research (поиск Google) для анализа макроэкономики и настроений рынка. Бот сам выбирает пары, расставляет сетки, использует аппаратный Trailing Stop от Binance для максимизации прибыли и имеет многоуровневый риск-менеджмент.

---

## 🚀 Key Features / Ключевые особенности

* 🧠 **AI Deep Research (Глубокий анализ ИИ):**
  * **EN:** The AI doesn't just look at technical indicators (RSI, ATR, EMA 200). It actively uses Google Search to analyze the FED rate, Crypto Fear & Greed Index, and coin-specific news (unlocks, hacks) before opening a position.
  * **RU:** ИИ анализирует не только технические индикаторы (RSI, ATR, EMA 200). Перед открытием сделки он гуглит макроэкономические данные, индекс страха и жадности, а также новости по конкретным монетам, защищая от входа перед плохими новостями.

* 🔥 **Smart Trailing Stop (Умный Трейлинг-стоп):**
  * **EN:** Profitable grids are not prematurely closed. The bot transfers control to Binance's hardware `TRAILING_STOP_MARKET` (with a 1.5% callback rate) to ride the trend and maximize profits automatically.
  * **RU:** Прибыльные позиции не закрываются раньше времени. Бот снимает сетку и передает управление аппаратному `TRAILING_STOP_MARKET` на серверах Binance (откат 1.5%), выжимая из тренда максимум.

* 🛡️ **Multi-level Risk Management (Многоуровневая защита):**
  * **EN:** Includes an Absolute Stop-Loss (e.g., stops trading if balance drops below 50%) and a Daily Drawdown limit (pauses trading for 12h if daily loss exceeds 10%).
  * **RU:** Включает абсолютный стоп-кран (выключается при потере 50% депозита) и дневной лимит просадки (пауза на 12 часов при убытке >10% за день).

* 🔄 **AI Fallback System (Безотказность ИИ):**
  * **EN:** Dual-model architecture with per-step timeouts. Prioritizes `Gemini 3.1 Pro`, but instantly falls back to `Gemini 3 Flash` if the primary model is slow or overloaded. Each fallback step has a 3-minute timeout to prevent blocking the trading cycle.
  * **RU:** Архитектура двух моделей с таймаутом на каждый шаг. Бот стучится в умную `Gemini 3.1 Pro`, но при сбоях API моментально переключается на безотказную `Gemini 3 Flash`. Каждый шаг имеет 3-минутный лимит, чтобы не блокировать торговый цикл.

* 📱 **Interactive Telegram Control (Управление через Telegram):**
  * **EN:** Real-time logging, daily PnL reports, and emergency commands (`/panic` to close all positions, `/pause` to halt execution). Commands are validated against both chat ID and user ID for security.
  * **RU:** Логирование в реальном времени, ежедневные отчеты о прибыли и экстренные команды (`/panic` для закрытия всех позиций по рынку, `/pause` для паузы). Команды валидируются по chat ID и user ID для безопасности.

---

## 🏗 System Architecture / Архитектура системы

**EN:** The project follows **Clean Architecture** principles. Business logic is strictly decoupled from external providers (Binance, Gemini, Telegram, PostgreSQL).
**RU:** Проект построен на принципах **Clean Architecture**. Бизнес-логика строго отделена от внешних провайдеров (Binance, Gemini, Telegram, PostgreSQL).

### Layer Breakdown / Разделение слоев:

* **`NetTrader.Domain`**
  * **EN:** Core entities (`GridSettings`, `MarketData`, `TradeSession`), interfaces, constants (`TradeStatus`, `SymbolConfig`), and FluentValidation rules. No external dependencies.
  * **RU:** Основные сущности, интерфейсы, константы (`TradeStatus`, `SymbolConfig`) и правила валидации FluentValidation. Ноль внешних зависимостей.
* **`NetTrader.Application`**
  * **EN:** Orchestrates trading logic via `GridTradingManager`. Handles mathematical calculations for grid levels and indicator enrichment via `IndicatorEnrichmentService`.
  * **RU:** Координирует торговую логику через `GridTradingManager`. Выполняет математические расчеты уровней сетки и обогащение индикаторами через `IndicatorEnrichmentService`.
* **`NetTrader.Infrastructure`**
  * **EN:** External service integrations: `BinanceFuturesClient`, `GeminiAdvisor`, `TelegramService`, and Entity Framework Core for PostgreSQL. Includes Polly for resilience.
  * **RU:** Интеграция внешних сервисов: API Binance, Gemini, Telegram и работа с базой данных PostgreSQL через EF Core. Включает Polly для отказоустойчивости.
* **`NetTrader.Worker`**
  * **EN:** The entry point. A background service running the autonomous trading loop and Telegram listener.
  * **RU:** Точка входа. Фоновая служба, запускающая цикл автономной торговли и слушатель Telegram-команд.

---

## ⚙️ Tech Stack / Технологии

* **Core:** C# / .NET 9 (Worker Service)
* **AI:** Google Gemini API (`gemini-3.1-pro-preview` & `gemini-3-flash-preview` + Google Search Tool)
* **Exchange:** Binance Futures (REST API, HMAC SHA256 signatures)
* **Database:** PostgreSQL (Entity Framework Core)
* **Indicators:** Skender.Stock.Indicators (RSI, ATR, EMA, ADX, MACD, Bollinger Bands)
* **Resilience:** Polly (Retry + Circuit Breaker)
* **Validation:** FluentValidation (AI output sanity checks)
* **Logging:** Serilog (Console + File + optional Seq)
* **DevOps:** Docker, GitHub Actions, Google Cloud Compute Engine

---

## ⚠️ Disclaimer / Отказ от ответственности

**EN:** This bot is for educational purposes. Trading cryptocurrency futures involves extremely high risk. The developers are not responsible for any financial losses. Always use the Binance Testnet before trading with real funds.

**RU:** Этот алгоритм создан исключительно в образовательных целях. Торговля криптовалютными фьючерсами сопряжена с экстремально высоким риском потери капитала. Разработчик не несет ответственности за финансовые убытки. Настоятельно рекомендуется использовать Binance Testnet перед запуском на реальных средствах.