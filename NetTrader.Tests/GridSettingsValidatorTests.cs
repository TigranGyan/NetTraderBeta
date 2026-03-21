using FluentAssertions;
using FluentValidation.TestHelper;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Validation;

namespace NetTrader.Tests;

/// <summary>
/// Тесты валидатора GridSettings.
/// Проверяют что ИИ-галлюцинации отсекаются до попадания в торговый движок.
/// </summary>
public class GridSettingsValidatorTests
{
    private readonly GridSettingsValidator _validator = new();

    // ═══ ВАЛИДНЫЕ СЦЕНАРИИ ═══

    [Fact]
    public void ValidGrid_Long_PassesValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "BTCUSDT",
            OrderType = 0,
            LowerPrice = 90000m,
            UpperPrice = 95000m,
            GridLevels = 10,
            TotalInvestment = 25m,
            Direction = 0, // Long
            StopLoss = 88000m, // Ниже LowerPrice ✓
            TakeProfit = 97000m
        };

        var result = _validator.TestValidate(grid);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ValidMarketLong_PassesValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "ETHUSDT",
            OrderType = 1,
            TotalInvestment = 20m,
            StopLoss = 3000m,
            TakeProfit = 3500m // TP > SL ✓
        };

        var result = _validator.TestValidate(grid);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ValidClosePosition_PassesValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "XRPUSDT",
            OrderType = 3,
            Rationale = "Bridge hack"
        };

        var result = _validator.TestValidate(grid);
        result.ShouldNotHaveAnyValidationErrors();
    }

    // ═══ AI ГАЛЛЮЦИНАЦИИ — должны быть отклонены ═══

    [Fact]
    public void Grid_UpperPriceBelowLowerPrice_FailsValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "BTCUSDT",
            OrderType = 0,
            LowerPrice = 100000m,
            UpperPrice = 90000m, // ❌ UpperPrice < LowerPrice!
            GridLevels = 10,
            TotalInvestment = 25m,
            Direction = 0,
            StopLoss = 85000m,
            TakeProfit = 105000m
        };

        var result = _validator.TestValidate(grid);
        result.ShouldHaveValidationErrorFor(x => x.UpperPrice);
    }

    [Fact]
    public void Grid_NegativePrices_FailsValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "BTCUSDT",
            OrderType = 0,
            LowerPrice = -100m, // ❌ Отрицательная цена
            UpperPrice = 100m,
            GridLevels = 10,
            TotalInvestment = 25m,
            Direction = 0,
            StopLoss = -200m,
            TakeProfit = 200m
        };

        var result = _validator.TestValidate(grid);
        result.ShouldHaveValidationErrorFor(x => x.LowerPrice);
    }

    [Fact]
    public void Grid_ZeroGridLevels_FailsValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "BTCUSDT",
            OrderType = 0,
            LowerPrice = 90000m,
            UpperPrice = 95000m,
            GridLevels = 0, // ❌ 0 уровней
            TotalInvestment = 25m,
            Direction = 0,
            StopLoss = 88000m,
            TakeProfit = 97000m
        };

        var result = _validator.TestValidate(grid);
        result.ShouldHaveValidationErrorFor(x => x.GridLevels);
    }

    [Fact]
    public void Grid_100GridLevels_FailsValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "BTCUSDT",
            OrderType = 0,
            LowerPrice = 90000m,
            UpperPrice = 95000m,
            GridLevels = 100, // ❌ Слишком много
            TotalInvestment = 25m,
            Direction = 0,
            StopLoss = 88000m,
            TakeProfit = 97000m
        };

        var result = _validator.TestValidate(grid);
        result.ShouldHaveValidationErrorFor(x => x.GridLevels);
    }

    [Fact]
    public void LongGrid_StopLossAboveLowerPrice_FailsValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "BTCUSDT",
            OrderType = 0,
            LowerPrice = 90000m,
            UpperPrice = 95000m,
            GridLevels = 10,
            TotalInvestment = 25m,
            Direction = 0, // Long
            StopLoss = 92000m, // ❌ SL выше LowerPrice для Long!
            TakeProfit = 97000m
        };

        var result = _validator.TestValidate(grid);
        result.ShouldHaveValidationErrorFor(x => x.StopLoss);
    }

    [Fact]
    public void ShortGrid_StopLossBelowUpperPrice_FailsValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "BTCUSDT",
            OrderType = 0,
            LowerPrice = 90000m,
            UpperPrice = 95000m,
            GridLevels = 10,
            TotalInvestment = 25m,
            Direction = 1, // Short
            StopLoss = 93000m, // ❌ SL ниже UpperPrice для Short!
            TakeProfit = 88000m
        };

        var result = _validator.TestValidate(grid);
        result.ShouldHaveValidationErrorFor(x => x.StopLoss);
    }

    [Fact]
    public void MarketLong_TpBelowSl_FailsValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "ETHUSDT",
            OrderType = 1, // Market Long
            TotalInvestment = 20m,
            StopLoss = 3500m,
            TakeProfit = 3000m // ❌ TP < SL для Long!
        };

        var result = _validator.TestValidate(grid);
        result.ShouldHaveValidationErrorFor(x => x.TakeProfit);
    }

    [Fact]
    public void MarketShort_SlBelowTp_FailsValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "ETHUSDT",
            OrderType = 2, // Market Short
            TotalInvestment = 20m,
            StopLoss = 3000m, // ❌ SL < TP для Short!
            TakeProfit = 3500m
        };

        var result = _validator.TestValidate(grid);
        result.ShouldHaveValidationErrorFor(x => x.StopLoss);
    }

    [Fact]
    public void EmptySymbol_FailsValidation()
    {
        var grid = new GridSettings
        {
            Symbol = "", // ❌
            OrderType = 1,
            TotalInvestment = 20m,
            StopLoss = 3000m,
            TakeProfit = 3500m
        };

        var result = _validator.TestValidate(grid);
        result.ShouldHaveValidationErrorFor(x => x.Symbol);
    }
}

/// <summary>
/// Тесты валидатора списка GridSettings (батч от ИИ).
/// </summary>
public class GridSettingsListValidatorTests
{
    private readonly GridSettingsListValidator _validator = new();

    [Fact]
    public void MoreThan3ActiveActions_FailsValidation()
    {
        var grids = new List<GridSettings>
        {
            new() { Symbol = "BTCUSDT", OrderType = 1, TotalInvestment = 20m, StopLoss = 90000m, TakeProfit = 100000m },
            new() { Symbol = "ETHUSDT", OrderType = 1, TotalInvestment = 20m, StopLoss = 3000m, TakeProfit = 3500m },
            new() { Symbol = "SOLUSDT", OrderType = 1, TotalInvestment = 20m, StopLoss = 100m, TakeProfit = 130m },
            new() { Symbol = "LINKUSDT", OrderType = 1, TotalInvestment = 20m, StopLoss = 10m, TakeProfit = 15m },
        };

        var result = _validator.TestValidate(grids);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void DuplicateCorrelationGroup_FailsValidation()
    {
        // ETH и SOL оба в Layer1 — нельзя одновременно
        var grids = new List<GridSettings>
        {
            new() { Symbol = "ETHUSDT", OrderType = 1, TotalInvestment = 20m, StopLoss = 3000m, TakeProfit = 3500m },
            new() { Symbol = "SOLUSDT", OrderType = 1, TotalInvestment = 20m, StopLoss = 100m, TakeProfit = 130m },
        };

        var result = _validator.TestValidate(grids);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ClosePositionsDontCountAsActive_PassesValidation()
    {
        var grids = new List<GridSettings>
        {
            new() { Symbol = "BTCUSDT", OrderType = 1, TotalInvestment = 20m, StopLoss = 90000m, TakeProfit = 100000m },
            new() { Symbol = "LINKUSDT", OrderType = 1, TotalInvestment = 20m, StopLoss = 10m, TakeProfit = 15m },
            new() { Symbol = "DOGEUSDT", OrderType = 1, TotalInvestment = 20m, StopLoss = 0.1m, TakeProfit = 0.15m },
            // ClosePosition не считается
            new() { Symbol = "XRPUSDT", OrderType = 3, Rationale = "Close" },
            new() { Symbol = "ETHUSDT", OrderType = 3, Rationale = "Close" },
        };

        var result = _validator.TestValidate(grids);
        // 3 активных + 2 закрытия = OK (max 3 active)
        result.Errors.Where(e => e.ErrorMessage.Contains("3 торговых")).Should().BeEmpty();
    }
}
