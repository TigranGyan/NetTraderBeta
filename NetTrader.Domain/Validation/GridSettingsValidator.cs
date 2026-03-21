using FluentValidation;
using NetTrader.Domain.Constants;
using NetTrader.Domain.Entities;

namespace NetTrader.Domain.Validation;

/// <summary>
/// Валидатор параметров сетки от ИИ.
/// Защита депозита от галлюцинаций LLM.
/// </summary>
public class GridSettingsValidator : AbstractValidator<GridSettings>
{
    public GridSettingsValidator()
    {
        // ═══ Общие правила для всех OrderType ═══
        RuleFor(x => x.Symbol)
            .NotEmpty()
            .WithMessage("Symbol не может быть пустым");

        RuleFor(x => x.OrderType)
            .InclusiveBetween(0, 3)
            .WithMessage("OrderType должен быть 0-3");

        RuleFor(x => x.TotalInvestment)
            .GreaterThan(0)
            .When(x => x.OrderType != 3)
            .WithMessage("TotalInvestment должен быть > 0");

        RuleFor(x => x.StopLoss)
            .GreaterThan(0)
            .When(x => x.OrderType != 3)
            .WithMessage("StopLoss должен быть > 0");

        RuleFor(x => x.TakeProfit)
            .GreaterThan(0)
            .When(x => x.OrderType != 3)
            .WithMessage("TakeProfit должен быть > 0");

        // ═══ Grid (OrderType=0) ═══
        When(x => x.OrderType == 0, () =>
        {
            RuleFor(x => x.UpperPrice)
                .GreaterThan(0)
                .WithMessage("Grid: UpperPrice должен быть > 0");

            RuleFor(x => x.LowerPrice)
                .GreaterThan(0)
                .WithMessage("Grid: LowerPrice должен быть > 0");

            RuleFor(x => x.UpperPrice)
                .GreaterThan(x => x.LowerPrice)
                .WithMessage("Grid: UpperPrice ({PropertyValue}) должен быть > LowerPrice");

            RuleFor(x => x.GridLevels)
                .InclusiveBetween(3, 50)
                .WithMessage("Grid: GridLevels должен быть 3-50, получено {PropertyValue}");

            RuleFor(x => x.Direction)
                .InclusiveBetween(0, 2)
                .WithMessage("Grid: Direction должен быть 0(Long), 1(Short), 2(Both)");

            RuleFor(x => x.StopLoss)
                .LessThan(x => x.LowerPrice)
                .When(x => x.Direction == 0)
                .WithMessage("Grid Long: StopLoss ({PropertyValue}) должен быть < LowerPrice");

            RuleFor(x => x.StopLoss)
                .GreaterThan(x => x.UpperPrice)
                .When(x => x.Direction == 1)
                .WithMessage("Grid Short: StopLoss ({PropertyValue}) должен быть > UpperPrice");
        });

        // ═══ Market Long (OrderType=1) ═══
        When(x => x.OrderType == 1, () =>
        {
            RuleFor(x => x.TakeProfit)
                .GreaterThan(x => x.StopLoss)
                .WithMessage("Market Long: TakeProfit ({PropertyValue}) должен быть > StopLoss");
        });

        // ═══ Market Short (OrderType=2) ═══
        When(x => x.OrderType == 2, () =>
        {
            RuleFor(x => x.StopLoss)
                .GreaterThan(x => x.TakeProfit)
                .WithMessage("Market Short: StopLoss ({PropertyValue}) должен быть > TakeProfit");
        });
    }
}

/// <summary>
/// Валидатор для списка GridSettings (батч от ИИ).
/// FIX #7: Использует SymbolConfig.CoinGroups вместо локальной копии.
/// FIX #17: Правило "max 3 actions" синхронизировано с промптом.
/// </summary>
public class GridSettingsListValidator : AbstractValidator<List<GridSettings>>
{
    public GridSettingsListValidator()
    {
        // FIX #17: Правило раскомментировано — промпт теперь тоже говорит "Max 3 actions"
        RuleFor(x => x)
            .Must(list => list.Count(g => g.OrderType != 3) <= 3)
            .WithMessage("Максимум 3 торговых действия за цикл (не считая ClosePosition)");

        RuleFor(x => x)
            .Must(NoDuplicateGroups)
            .WithMessage("Не более 1 монеты из одной корреляционной группы");

        RuleForEach(x => x).SetValidator(new GridSettingsValidator());
    }

    private static bool NoDuplicateGroups(List<GridSettings> grids)
    {
        var activeGrids = grids.Where(g => g.OrderType != 3).ToList();

        // FIX #7: Single source — SymbolConfig.CoinGroups
        var groups = activeGrids
            .Where(g => SymbolConfig.CoinGroups.ContainsKey(g.Symbol))
            .GroupBy(g => SymbolConfig.CoinGroups[g.Symbol]);

        return groups.All(group => group.Count() <= 1);
    }
}