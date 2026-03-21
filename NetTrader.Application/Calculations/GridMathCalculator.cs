using NetTrader.Domain.Entities;
using System;
using System.Collections.Generic;

namespace NetTrader.Application.Calculations;

public static class GridMathCalculator
{
    private static decimal RoundToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Round(value / step) * step;
    }

    public static List<GridOrder> CalculateOrders(GridSettings settings, MarketData marketData)
    {
        var orders = new List<GridOrder>();
        decimal investmentPerLevel = settings.TotalInvestment / settings.GridLevels;

        for (int i = 0; i < settings.GridLevels; i++)
        {
            decimal rawPrice = settings.LowerPrice + (settings.GridStep * i);
            decimal price = RoundToStep(rawPrice, marketData.TickSize);

            string side = price < marketData.CurrentPrice ? "BUY" : "SELL";

            if (settings.Direction == 0 && side == "SELL") continue;
            if (settings.Direction == 1 && side == "BUY") continue;
            if (price == marketData.CurrentPrice) continue;
            if (price <= 0) continue;

            decimal rawQuantity = investmentPerLevel / price;
            decimal orderValue = price * rawQuantity;
            decimal finalQuantity;

            if (orderValue < marketData.MinNotional)
            {
                // Увеличиваем quantity до MinNotional.
                // FIX: Убрана проверка realCost > TotalInvestment * 0.5 — она сравнивала
                // НОТИОНАЛ ($139) с МАРЖОЙ ($30), что неправильно при плече 10x.
                // Если маржи не хватит — Binance API сам вернёт ошибку,
                // и PlaceGridAsync откатит все ордера.
                finalQuantity = Math.Ceiling(marketData.MinNotional / price / marketData.StepSize) * marketData.StepSize;
            }
            else
            {
                finalQuantity = RoundToStep(rawQuantity, marketData.StepSize);
            }

            if (finalQuantity > 0)
            {
                orders.Add(new GridOrder
                {
                    Price = price,
                    Quantity = finalQuantity,
                    Side = side
                });
            }
        }
        return orders;
    }
}