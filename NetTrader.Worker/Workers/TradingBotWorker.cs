using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTrader.Application.Services;
using NetTrader.Domain.Constants;
using NetTrader.Domain.Interfaces;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Options;

namespace NetTrader.Worker.Workers;

public class TradingBotWorker : BackgroundService
{
    private readonly ILogger<TradingBotWorker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly BotState _botState;
    private readonly TradingOptions _tradingOptions;
    private DateTime _lastDailyReport = DateTime.UtcNow.Date;
    private decimal _startDayBalance = -1;

    private static readonly Meter TradingMeter = new("NetTrader.Trading", "1.0");
    private static readonly Counter<int> CycleCounter = TradingMeter.CreateCounter<int>("trading.cycles.total", description: "Total trading cycles");
    private static readonly Counter<int> ErrorCounter = TradingMeter.CreateCounter<int>("trading.errors.total", description: "Total API errors");
    private static readonly Histogram<double> CycleDuration = TradingMeter.CreateHistogram<double>("trading.cycle.duration_ms", description: "Cycle duration in ms");

    public TradingBotWorker(
        ILogger<TradingBotWorker> logger,
        IServiceProvider serviceProvider,
        BotState botState,
        IOptions<TradingOptions> tradingOptions)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _botState = botState;
        _tradingOptions = tradingOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 NetTrader v7: IOptions + Polly + FluentValidation + Metrics");
        decimal absoluteStopBalance = 0m;
        var checkInterval = TimeSpan.FromMinutes(_tradingOptions.CheckIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStart = DateTime.UtcNow;

            try
            {
                await ProcessPendingCommandsAsync();

                using var scope = _serviceProvider.CreateScope();
                var market = scope.ServiceProvider.GetRequiredService<IMarketDataProvider>();
                var ai = scope.ServiceProvider.GetRequiredService<IAiAdvisor>();
                var manager = scope.ServiceProvider.GetRequiredService<GridTradingManager>();
                var executor = scope.ServiceProvider.GetRequiredService<IOrderExecutor>();
                var telegram = scope.ServiceProvider.GetRequiredService<ITelegramService>();
                var tradeRepo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
                var macroAnalyzer = scope.ServiceProvider.GetRequiredService<NetTrader.Infrastructure.MacroServices.MacroAnalyzer>();
                // FIX #10: Indicator enrichment — extracted from this worker
                var enricher = scope.ServiceProvider.GetRequiredService<IndicatorEnrichmentService>();

                var macroStatus = await macroAnalyzer.GetMacroStatusAsync();
                _logger.LogInformation("{MacroStatus}", macroStatus);

                // ═══ Экстренная остановка ═══
                if (_botState.EmergencyStopRequested)
                {
                    await EmergencyShutdown(executor, tradeRepo, telegram, await executor.GetMarginBalanceAsync(), "/panic");
                    _botState.EmergencyStopRequested = false;
                }

                if (_botState.IsPaused) { await Task.Delay(checkInterval, stoppingToken); continue; }

                decimal currentBalance = await executor.GetMarginBalanceAsync();
                if (absoluteStopBalance == 0) absoluteStopBalance = currentBalance;
                if (_startDayBalance < 0) _startDayBalance = currentBalance;

                if (currentBalance < absoluteStopBalance * _tradingOptions.EmergencyStopBalancePercent)
                {
                    await EmergencyShutdown(executor, tradeRepo, telegram, currentBalance,
                        $"Баланс < {_tradingOptions.EmergencyStopBalancePercent * 100:F0}% ({currentBalance:F2})");
                    break;
                }

                // ═══ Дневной отчёт ═══
                if (DateTime.UtcNow.Date != _lastDailyReport.Date)
                {
                    decimal dp = currentBalance - _startDayBalance;
                    decimal dpPct = _startDayBalance > 0 ? dp / _startDayBalance * 100m : 0m;
                    int closed = await tradeRepo.GetClosedGridsCountSinceAsync(DateTime.UtcNow.Date);
                    var rpt = $"📊 *ОТЧЁТ*\n💰 {currentBalance:F2}$\n📈 {dp:+0.00;-0.00}$ ({dpPct:+0.0;-0.0}%)\n✅ Закрыто: {closed}";

                    if (dpPct <= -(decimal)_tradingOptions.DailyDrawdownPausePercent)
                    {
                        _botState.IsPaused = true;
                        await telegram.SendMessageAsync(rpt + $"\n⚠️ Пауза {_tradingOptions.DrawdownPauseHours}ч");
                        await Task.Delay(TimeSpan.FromHours(_tradingOptions.DrawdownPauseHours), stoppingToken);
                        _botState.IsPaused = false;
                        continue;
                    }
                    await telegram.SendMessageAsync(rpt);
                    _lastDailyReport = DateTime.UtcNow.Date;
                    _startDayBalance = currentBalance;
                }

                // ═══ ЗАГРУЗКА МОНЕТ ═══
                var candidates = await market.GetTopVolatilityPairsAsync(_tradingOptions.TopPairsLimit);
                if (candidates.Count == 0) { await Task.Delay(checkInterval, stoppingToken); continue; }

                _logger.LogInformation("📊 {Count} монет. Обогащаю...", candidates.Count);

                // ═══ FIX #10: Обогащение индикаторами — вынесено в сервис ═══
                // FIX #19: CancellationToken передаётся для graceful shutdown
                await enricher.EnrichAllAsync(candidates, stoppingToken);

                // ═══ ИИ ═══
                _logger.LogInformation("🤖 ИИ анализирует {Count} кандидатов...", candidates.Count);
                var grids = await ai.AnalyzeAndSelectGridsAsync(candidates, currentBalance);
                _logger.LogInformation("📋 {Count} пар. Обновляю...", grids.Count);
                await manager.UpdateGridsAsync(grids);

                CycleCounter.Add(1);
                var elapsed = (DateTime.UtcNow - cycleStart).TotalMilliseconds;
                CycleDuration.Record(elapsed);
                _logger.LogInformation("✅ Цикл за {Ms:F0}ms. Баланс: {B:F2}$", elapsed, currentBalance);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // FIX #19: Graceful shutdown — не логируем как ошибку
                _logger.LogInformation("🛑 Торговый цикл остановлен (graceful shutdown).");
                break;
            }
            catch (Exception ex)
            {
                ErrorCounter.Add(1);
                _logger.LogError(ex, "❌ Ошибка цикла");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }

            await Task.Delay(checkInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingCommandsAsync()
    {
        while (_botState.TryReadCommand(out var command) && command != null)
        {
            _logger.LogInformation("📬 Команда: {Type} ({Arg})", command.Type, command.Argument);

            switch (command.Type)
            {
                case BotCommandType.Pause:
                    _botState.IsPaused = true;
                    break;
                case BotCommandType.Resume:
                    _botState.IsPaused = false;
                    break;
                case BotCommandType.Panic:
                    _botState.EmergencyStopRequested = true;
                    break;
                case BotCommandType.SetLeverage when int.TryParse(command.Argument, out int lev):
                    _botState.Leverage = lev;
                    break;
                case BotCommandType.Close when !string.IsNullOrEmpty(command.Argument):
                    break;
            }
        }
    }

    private async Task EmergencyShutdown(IOrderExecutor ex, ITradeRepository repo, ITelegramService tg, decimal bal, string reason)
    {
        await tg.SendMessageAsync($"🚨 *СТОП*\n{reason}\nБаланс: {bal:F2}$");
        foreach (var s in await repo.GetActiveSessionsAsync())
        {
            try
            {
                await ex.CancelAllGridOrdersAsync(s.Symbol);
                await ex.ClosePositionAsync(s.Symbol);
                s.Status = TradeStatus.EmergencyStopped; // FIX #11
                s.ClosedAt = DateTime.UtcNow;
                await repo.UpdateSessionAsync(s);
            }
            catch { }
        }
        await tg.SendMessageAsync("✅ Всё закрыто.");
    }
}