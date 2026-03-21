using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NetTrader.Application.Services;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Validation;

namespace NetTrader.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<GridTradingManager>();

        // FIX #10: Indicator enrichment — ранее 80 строк в TradingBotWorker
        services.AddScoped<IndicatorEnrichmentService>();

        // FluentValidation — санити-проверки ИИ
        services.AddScoped<IValidator<GridSettings>, GridSettingsValidator>();
        services.AddScoped<IValidator<List<GridSettings>>, GridSettingsListValidator>();

        return services;
    }
}