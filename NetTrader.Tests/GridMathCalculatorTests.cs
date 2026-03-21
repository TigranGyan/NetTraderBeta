using FluentAssertions;
using NetTrader.Application.Calculations;
using NetTrader.Domain.Entities;

namespace NetTrader.Tests;

/// <summary>
/// Юнит-тесты для GridMathCalculator.
/// Покрывают: базовый расчёт, округление, MinNotional, крайние случаи.
/// </summary>
public class GridMathCalculatorTests
{
    private static MarketData CreateMarketData(
        decimal currentPrice = 100m,
        decimal tickSize = 0.01m,
        decimal stepSize = 0.001m,
        decimal minNotional = 5m)
    {
        return new MarketData
        {
            Symbol = "TESTUSDT",
            CurrentPrice = currentPrice,
            TickSize = tickSize,
            StepSize = stepSize,
            MinNotional = minNotional
        };
    }

    private static GridSettings CreateGridSettings(
        decimal lowerPrice = 90m,
        decimal upperPrice = 110m,
        int gridLevels = 10,
        decimal totalInvestment = 100m,
        int direction = 0) // 0=Long
    {
        return new GridSettings
        {
            Symbol = "TESTUSDT",
            LowerPrice = lowerPrice,
            UpperPrice = upperPrice,
            GridLevels = gridLevels,
            TotalInvestment = totalInvestment,
            Direction = direction,
            StopLoss = lowerPrice - 5m,
            TakeProfit = upperPrice + 5m
        };
    }

    // ═══ БАЗОВЫЕ ТЕСТЫ ═══

    [Fact]
    public void CalculateOrders_BasicLongGrid_ReturnsOnlyBuyOrders()
    {
        // Arrange: Long grid (Direction=0), цена=100, сетка 90-110
        var settings = CreateGridSettings(direction: 0);
        var market = CreateMarketData(currentPrice: 100m);

        // Act
        var orders = GridMathCalculator.CalculateOrders(settings, market);

        // Assert: Long grid — только BUY ордера ниже текущей цены
        orders.Should().NotBeEmpty();
        orders.Should().OnlyContain(o => o.Side == "BUY");
        orders.Should().OnlyContain(o => o.Price < 100m);
    }

    [Fact]
    public void CalculateOrders_ShortGrid_ReturnsOnlySellOrders()
    {
        var settings = CreateGridSettings(direction: 1); // Short
        var market = CreateMarketData(currentPrice: 100m);

        var orders = GridMathCalculator.CalculateOrders(settings, market);

        orders.Should().NotBeEmpty();
        orders.Should().OnlyContain(o => o.Side == "SELL");
        orders.Should().OnlyContain(o => o.Price > 100m);
    }

    [Fact]
    public void CalculateOrders_BothDirection_ReturnsBuyAndSellOrders()
    {
        var settings = CreateGridSettings(direction: 2); // Both
        var market = CreateMarketData(currentPrice: 100m);

        var orders = GridMathCalculator.CalculateOrders(settings, market);

        orders.Should().NotBeEmpty();
        orders.Should().Contain(o => o.Side == "BUY");
        orders.Should().Contain(o => o.Side == "SELL");
    }

    // ═══ ТЕСТЫ ОКРУГЛЕНИЯ ═══

    [Fact]
    public void CalculateOrders_PricesRoundedToTickSize()
    {
        var settings = CreateGridSettings();
        var market = CreateMarketData(tickSize: 0.1m);

        var orders = GridMathCalculator.CalculateOrders(settings, market);

        foreach (var order in orders)
        {
            // Цена должна быть кратна tickSize (0.1)
            (order.Price % 0.1m).Should().Be(0m,
                $"Цена {order.Price} должна быть кратна tickSize 0.1");
        }
    }

    [Fact]
    public void CalculateOrders_QuantitiesRoundedToStepSize()
    {
        var settings = CreateGridSettings();
        var market = CreateMarketData(stepSize: 0.01m);

        var orders = GridMathCalculator.CalculateOrders(settings, market);

        foreach (var order in orders)
        {
            decimal remainder = order.Quantity % 0.01m;
            remainder.Should().BeApproximately(0m, 0.000001m,
                $"Quantity {order.Quantity} должно быть кратно stepSize 0.01");
        }
    }

    // ═══ ТЕСТЫ MinNotional ═══

    [Fact]
    public void CalculateOrders_SmallInvestment_IncreasesQuantityToMinNotional()
    {
        // Маленькая инвестиция: 10$ на 10 уровней = 1$ на уровень
        // MinNotional = 5$ → quantity должен быть увеличен
        var settings = CreateGridSettings(totalInvestment: 10m, gridLevels: 10);
        var market = CreateMarketData(currentPrice: 100m, minNotional: 5m);

        var orders = GridMathCalculator.CalculateOrders(settings, market);

        foreach (var order in orders)
        {
            decimal notional = order.Price * order.Quantity;
            notional.Should().BeGreaterOrEqualTo(market.MinNotional,
                $"Нотионал {notional} для {order.Side} @ {order.Price} должен быть >= MinNotional {market.MinNotional}");
        }
    }

    // ═══ КРАЙНИЕ СЛУЧАИ ═══

    [Fact]
    public void CalculateOrders_PriceEqualsCurrentPrice_Skipped()
    {
        // Если уровень сетки точно совпадает с текущей ценой — пропускается
        var settings = CreateGridSettings(lowerPrice: 100m, upperPrice: 110m, gridLevels: 2);
        var market = CreateMarketData(currentPrice: 100m);

        var orders = GridMathCalculator.CalculateOrders(settings, market);

        orders.Should().NotContain(o => o.Price == 100m);
    }

    [Fact]
    public void CalculateOrders_ZeroGridLevels_ReturnsEmptyOrSingle()
    {
        var settings = CreateGridSettings(gridLevels: 1);
        var market = CreateMarketData(currentPrice: 100m);

        // gridLevels=1, GridStep = (110-90)/(1-1=1) = 20. Один ордер на 90.
        var orders = GridMathCalculator.CalculateOrders(settings, market);

        orders.Count.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void CalculateOrders_NegativePrice_Skipped()
    {
        // Сетка с очень низкими ценами — отрицательные отфильтрованы
        var settings = CreateGridSettings(lowerPrice: -10m, upperPrice: 10m, gridLevels: 5);
        var market = CreateMarketData(currentPrice: 5m);

        var orders = GridMathCalculator.CalculateOrders(settings, market);

        orders.Should().OnlyContain(o => o.Price > 0);
    }

    [Fact]
    public void CalculateOrders_AllOrdersHavePositiveQuantity()
    {
        var settings = CreateGridSettings();
        var market = CreateMarketData();

        var orders = GridMathCalculator.CalculateOrders(settings, market);

        orders.Should().OnlyContain(o => o.Quantity > 0);
    }

    // ═══ ТЕСТ ФОРМУЛЫ: gridStep ═══

    [Theory]
    [InlineData(90, 110, 10, 2.2222)] // (110-90)/(10-1) = 2.222...
    [InlineData(100, 200, 5, 25)]      // (200-100)/(5-1) = 25
    [InlineData(50, 60, 2, 10)]        // (60-50)/(2-1) = 10
    public void GridSettings_GridStep_CalculatedCorrectly(
        decimal lower, decimal upper, int levels, decimal expectedStep)
    {
        var settings = new GridSettings
        {
            LowerPrice = lower,
            UpperPrice = upper,
            GridLevels = levels
        };

        settings.GridStep.Should().BeApproximately(expectedStep, 0.001m);
    }
}
