using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NetTrader.Application.Services;
using NetTrader.Domain.Constants;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Interfaces;
using NetTrader.Domain.Options;
using NetTrader.Domain.Validation;

namespace NetTrader.Tests;

/// <summary>
/// Тесты GridTradingManager с моками.
/// Проверяют оркестрацию без реальных запросов к бирже.
///
/// UPDATED for:
/// - Fix #2: CancelLimitOrdersOnlyAsync / CancelAlgoOrdersAsync
/// - Fix #9: Idempotent Pending → Active session flow
/// - Fix #11: TradeStatus constants
/// </summary>
public class GridTradingManagerTests
{
    private readonly Mock<IMarketDataProvider> _marketMock = new();
    private readonly Mock<IOrderExecutor> _executorMock = new();
    private readonly Mock<ITelegramService> _telegramMock = new();
    private readonly Mock<ITradeRepository> _repoMock = new();
    private readonly GridTradingManager _manager;

    public GridTradingManagerTests()
    {
        var tradingOptions = Options.Create(new TradingOptions
        {
            TrailingActivationRoiPercent = 3.0m,
            TrailingCallbackRatePercent = 1.5m,
            MinRoiPercentToCloseWhenAiDrops = 0.0m,
            MaxMarginUsagePercent = 0.8m
        });

        _manager = new GridTradingManager(
            _marketMock.Object,
            _executorMock.Object,
            new Mock<ILogger<GridTradingManager>>().Object,
            _telegramMock.Object,
            _repoMock.Object,
            tradingOptions,
            new GridSettingsValidator(),
            new GridSettingsListValidator()
        );

        // Default stubs
        _repoMock.Setup(r => r.GetActiveSessionsAsync())
            .ReturnsAsync(new List<TradeSession>());
        _executorMock.Setup(e => e.GetAvailableBalanceAsync())
            .ReturnsAsync(100m);

        // FIX #2: New granular cancel methods — default to success
        _executorMock.Setup(e => e.CancelLimitOrdersOnlyAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _executorMock.Setup(e => e.CancelAlgoOrdersAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task UpdateGridsAsync_InvalidGrids_SkipsAllInvalid()
    {
        var badGrids = new List<GridSettings>
        {
            new()
            {
                Symbol = "BTCUSDT",
                OrderType = 0,
                LowerPrice = 100000m,
                UpperPrice = 90000m, // ❌ Upper < Lower
                GridLevels = 10,
                TotalInvestment = 25m,
                Direction = 0,
                StopLoss = 85000m,
                TakeProfit = 105000m
            }
        };

        await _manager.UpdateGridsAsync(badGrids);

        // Ордера НЕ должны выставляться
        _executorMock.Verify(e => e.PlaceGridAsync(
            It.IsAny<string>(), It.IsAny<List<GridOrder>>(),
            It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);

        // Telegram должен получить предупреждение
        _telegramMock.Verify(t => t.SendMessageAsync(
            It.Is<string>(s => s.Contains("отклонены"))), Times.Once);
    }

    [Fact]
    public async Task UpdateGridsAsync_ValidGrid_PlacesOrders()
    {
        var grids = new List<GridSettings>
        {
            new()
            {
                Symbol = "LINKUSDT",
                OrderType = 0,
                LowerPrice = 10m,
                UpperPrice = 15m,
                GridLevels = 10,
                TotalInvestment = 25m,
                Direction = 0,
                StopLoss = 9m,
                TakeProfit = 17m
            }
        };

        _marketMock.Setup(m => m.GetCurrentMarketDataAsync("LINKUSDT"))
            .ReturnsAsync(new MarketData
            {
                Symbol = "LINKUSDT",
                CurrentPrice = 12m,
                TickSize = 0.01m,
                StepSize = 0.01m,
                MinNotional = 5m
            });

        await _manager.UpdateGridsAsync(grids);

        // FIX #2: Re-gridding uses CancelLimitOrdersOnlyAsync (not CancelAllGridOrdersAsync)
        _executorMock.Verify(e => e.CancelLimitOrdersOnlyAsync("LINKUSDT"), Times.Once);

        // Ордера ДОЛЖНЫ быть выставлены
        _executorMock.Verify(e => e.PlaceGridAsync(
            "LINKUSDT", It.IsAny<List<GridOrder>>(),
            It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Once);

        // FIX #9: Session created first as Pending, then updated to Active
        _repoMock.Verify(r => r.AddSessionAsync(
            It.Is<TradeSession>(s => s.Symbol == "LINKUSDT" && s.Status == TradeStatus.Pending)), Times.Once);
        _repoMock.Verify(r => r.UpdateSessionAsync(
            It.Is<TradeSession>(s => s.Symbol == "LINKUSDT" && s.Status == TradeStatus.Active)), Times.Once);
    }

    [Fact]
    public async Task UpdateGridsAsync_ClosePosition_CancelsAndCloses()
    {
        var existingSession = new TradeSession
        {
            Symbol = "XRPUSDT",
            Status = TradeStatus.Active,
            OpenedAt = DateTime.UtcNow.AddHours(-2)
        };

        _repoMock.Setup(r => r.GetActiveSessionsAsync())
            .ReturnsAsync(new List<TradeSession> { existingSession });

        var grids = new List<GridSettings>
        {
            new()
            {
                Symbol = "XRPUSDT",
                OrderType = 3,
                Rationale = "Bridge hack"
            }
        };

        await _manager.UpdateGridsAsync(grids);

        // FIX #4: CancelAllGridOrdersAsync now handles both limit + algo
        _executorMock.Verify(e => e.CancelAllGridOrdersAsync("XRPUSDT"), Times.Once);
        _executorMock.Verify(e => e.ClosePositionAsync("XRPUSDT"), Times.Once);
        _repoMock.Verify(r => r.UpdateSessionAsync(
            It.Is<TradeSession>(s => s.Status == TradeStatus.ClosedByAI)), Times.Once);
    }

    [Fact]
    public async Task UpdateGridsAsync_ProfitablePosition_ActivatesTrailing()
    {
        var session = new TradeSession
        {
            Symbol = "ETHUSDT",
            Status = TradeStatus.Active,
            TotalInvestment = 20m,
            LowerPrice = 3000m,
            UpperPrice = 3200m,
            StopLoss = 2900m,
            TakeProfit = 3400m
        };

        _repoMock.Setup(r => r.GetActiveSessionsAsync())
            .ReturnsAsync(new List<TradeSession> { session });

        // PnL = +1$ на 20$ инвестиции = +5% → выше TrailingActivationRoiPercent (3%)
        _executorMock.Setup(e => e.GetPositionInfoAsync("ETHUSDT"))
            .ReturnsAsync((1m, 0.01m));

        // ИИ НЕ рекомендует ETHUSDT → должен уйти в трейлинг
        var grids = new List<GridSettings>
        {
            new()
            {
                Symbol = "BTCUSDT",
                OrderType = 1,
                TotalInvestment = 20m,
                StopLoss = 90000m,
                TakeProfit = 100000m
            }
        };

        _marketMock.Setup(m => m.GetCurrentMarketDataAsync("BTCUSDT"))
            .ReturnsAsync(new MarketData
            {
                Symbol = "BTCUSDT",
                CurrentPrice = 95000m,
                TickSize = 0.1m,
                StepSize = 0.001m,
                MinNotional = 5m
            });

        await _manager.UpdateGridsAsync(grids);

        // Trailing stop должен быть выставлен
        _executorMock.Verify(e => e.PlaceTrailingStopAsync("ETHUSDT", 0.01m, 1.5m), Times.Once);

        // FIX #2: Trailing activation uses granular cancels
        _executorMock.Verify(e => e.CancelLimitOrdersOnlyAsync("ETHUSDT"), Times.Once);
        _executorMock.Verify(e => e.CancelAlgoOrdersAsync("ETHUSDT"), Times.Once);

        _repoMock.Verify(r => r.UpdateSessionAsync(
            It.Is<TradeSession>(s => s.Symbol == "ETHUSDT" && s.Status == TradeStatus.Trailing)), Times.Once);
    }

    [Fact]
    public async Task UpdateGridsAsync_MarginExceeded_SkipsOrder()
    {
        _executorMock.Setup(e => e.GetAvailableBalanceAsync())
            .ReturnsAsync(10m);

        var grids = new List<GridSettings>
        {
            new()
            {
                Symbol = "BTCUSDT",
                OrderType = 1,
                TotalInvestment = 25m,
                StopLoss = 90000m,
                TakeProfit = 100000m
            }
        };

        await _manager.UpdateGridsAsync(grids);

        // Ордер НЕ должен быть выставлен из-за нехватки маржи
        _executorMock.Verify(e => e.PlaceMarketOrderAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()), Times.Never);
    }

    // ═══ NEW TESTS FOR FIX #9: Idempotent sessions ═══

    [Fact]
    public async Task UpdateGridsAsync_PlaceGridFails_SessionStaysPendingIfPositionExists()
    {
        var grids = new List<GridSettings>
        {
            new()
            {
                Symbol = "LINKUSDT",
                OrderType = 0,
                LowerPrice = 10m,
                UpperPrice = 15m,
                GridLevels = 10,
                TotalInvestment = 25m,
                Direction = 0,
                StopLoss = 9m,
                TakeProfit = 17m
            }
        };

        _marketMock.Setup(m => m.GetCurrentMarketDataAsync("LINKUSDT"))
            .ReturnsAsync(new MarketData
            {
                Symbol = "LINKUSDT",
                CurrentPrice = 12m,
                TickSize = 0.01m,
                StepSize = 0.01m,
                MinNotional = 5m
            });

        // PlaceGridAsync throws (partial fill scenario)
        _executorMock.Setup(e => e.PlaceGridAsync(
            "LINKUSDT", It.IsAny<List<GridOrder>>(),
            It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ThrowsAsync(new Exception("Binance error at order #5"));

        // After failure, position exists (partial fill)
        _executorMock.Setup(e => e.GetPositionInfoAsync("LINKUSDT"))
            .ReturnsAsync((0m, 0.5m)); // Has position

        await _manager.UpdateGridsAsync(grids);

        // Session should be created as Pending
        _repoMock.Verify(r => r.AddSessionAsync(
            It.Is<TradeSession>(s => s.Status == TradeStatus.Pending)), Times.Once);

        // Session should NOT be updated to Closed (position exists → stay Pending)
        _repoMock.Verify(r => r.UpdateSessionAsync(
            It.Is<TradeSession>(s => s.Status == TradeStatus.Closed)), Times.Never);
    }

    [Fact]
    public async Task UpdateGridsAsync_PlaceGridFails_SessionClosedIfNoPosition()
    {
        var grids = new List<GridSettings>
        {
            new()
            {
                Symbol = "LINKUSDT",
                OrderType = 0,
                LowerPrice = 10m,
                UpperPrice = 15m,
                GridLevels = 10,
                TotalInvestment = 25m,
                Direction = 0,
                StopLoss = 9m,
                TakeProfit = 17m
            }
        };

        _marketMock.Setup(m => m.GetCurrentMarketDataAsync("LINKUSDT"))
            .ReturnsAsync(new MarketData
            {
                Symbol = "LINKUSDT",
                CurrentPrice = 12m,
                TickSize = 0.01m,
                StepSize = 0.01m,
                MinNotional = 5m
            });

        _executorMock.Setup(e => e.PlaceGridAsync(
            "LINKUSDT", It.IsAny<List<GridOrder>>(),
            It.IsAny<decimal>(), It.IsAny<decimal>()))
            .ThrowsAsync(new Exception("Binance rejected all orders"));

        // After failure, no position (nothing filled)
        _executorMock.Setup(e => e.GetPositionInfoAsync("LINKUSDT"))
            .ReturnsAsync((0m, 0m));

        await _manager.UpdateGridsAsync(grids);

        // Session created as Pending, then cleaned up to Closed
        _repoMock.Verify(r => r.AddSessionAsync(
            It.Is<TradeSession>(s => s.Status == TradeStatus.Pending)), Times.Once);
        _repoMock.Verify(r => r.UpdateSessionAsync(
            It.Is<TradeSession>(s => s.Status == TradeStatus.Closed)), Times.Once);
    }
}