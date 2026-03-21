using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Interfaces;
using NetTrader.Domain.Options;
using NetTrader.Infrastructure.AiServices;
using NetTrader.Infrastructure.Exchanges;
using NetTrader.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using NetTrader.Infrastructure.Repositories;
using Polly;
using Polly.Extensions.Http;

namespace NetTrader.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // ═══════════════════════════════════════
        // 1. IOptions<T> — типизированные конфигурации с валидацией при старте
        // ═══════════════════════════════════════
        services.AddOptions<BinanceOptions>()
            .Bind(configuration.GetSection(BinanceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.PostConfigure<BinanceOptions>(opts =>
        {
            if (string.IsNullOrEmpty(opts.ApiKey))
            {
                opts.ApiKey = configuration["ExchangeApi:ApiKey"] ?? opts.ApiKey;
                opts.ApiSecret = configuration["ExchangeApi:ApiSecret"] ?? opts.ApiSecret;
                opts.BaseUrl = configuration["ExchangeApi:BaseUrl"] ?? opts.BaseUrl;
            }
        });

        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AiApiOptions>()
            .Bind(configuration.GetSection(AiApiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<TradingOptions>()
            .Bind(configuration.GetSection(TradingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // ═══════════════════════════════════════
        // 2. BotState — singleton, потокобезопасный
        // ═══════════════════════════════════════
        services.AddSingleton(sp =>
        {
            var tradingOpts = sp.GetRequiredService<IOptions<TradingOptions>>().Value;
            return new BotState(tradingOpts.DefaultLeverage);
        });

        // ═══════════════════════════════════════
        // 3. Polly — политики отказоустойчивости
        // ═══════════════════════════════════════
        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => (int)msg.StatusCode == 429)
            .WaitAndRetryAsync(3, retryAttempt =>
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryAttempt, _) =>
                {
                    Console.WriteLine($"⏳ Polly Retry #{retryAttempt} через {timespan.TotalSeconds:F0}s " +
                        $"(Status: {outcome.Result?.StatusCode})");
                });

        var circuitBreakerPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromMinutes(1),
                onBreak: (_, duration) =>
                    Console.WriteLine($"🔌 Circuit OPEN на {duration.TotalSeconds:F0}s"),
                onReset: () =>
                    Console.WriteLine("✅ Circuit CLOSED — восстановлено"));

        var combinedPolicy = Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);

        // ═══════════════════════════════════════
        // 4. Binance HttpClient + Polly
        //
        // FIX #5: Named HttpClient + manual Scoped registration.
        // Ensures ONE BinanceFuturesClient per scope, aliased by both interfaces.
        // Previous version used AddHttpClient<BinanceFuturesClient> + AddScoped that
        // resolved itself → StackOverflow.
        // ═══════════════════════════════════════
        services.AddHttpClient("BinanceClient", (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<BinanceOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
        })
        .AddPolicyHandler(combinedPolicy);

        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient("BinanceClient");
            var options = sp.GetRequiredService<IOptions<BinanceOptions>>();
            var botState = sp.GetRequiredService<BotState>();
            var logger = sp.GetRequiredService<ILogger<BinanceFuturesClient>>();
            var client = new BinanceFuturesClient(httpClient, options, botState, logger);
            var telegram = sp.GetRequiredService<ITelegramService>();
            client.SetTelegramService(telegram);
            return client;
        });

        services.AddScoped<IMarketDataProvider>(sp => sp.GetRequiredService<BinanceFuturesClient>());
        services.AddScoped<IOrderExecutor>(sp => sp.GetRequiredService<BinanceFuturesClient>());

        // ═══════════════════════════════════════
        // 5. Telegram HttpClient + Polly
        // ═══════════════════════════════════════
        services.AddHttpClient<ITelegramService, TelegramService>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddPolicyHandler(combinedPolicy);

        // ═══════════════════════════════════════
        // 6. Repository
        // ═══════════════════════════════════════
        services.AddScoped<ITradeRepository, TradeRepository>();

        // ═══════════════════════════════════════
        // 7. Macro analyzer + Polly
        // ═══════════════════════════════════════
        services.AddHttpClient<MacroServices.MacroAnalyzer>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        })
        .AddPolicyHandler(retryPolicy);

        // ═══════════════════════════════════════
        // 8. Gemini AI — Polly + увеличенный таймаут
        // ═══════════════════════════════════════
        services.AddHttpClient("GeminiAI", (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<AiApiOptions>>().Value;
            client.Timeout = TimeSpan.FromMinutes(opts.TimeoutMinutes);
        })
        .AddPolicyHandler(retryPolicy);

        services.AddScoped<IAiAdvisor>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("GeminiAI");
            var macroAnalyzer = sp.GetRequiredService<MacroServices.MacroAnalyzer>();
            var tradeRepo = sp.GetRequiredService<ITradeRepository>();
            var orderExecutor = sp.GetRequiredService<IOrderExecutor>();
            var aiOptions = sp.GetRequiredService<IOptions<AiApiOptions>>();
            var logger = sp.GetRequiredService<ILogger<GeminiAdvisor>>();

            return new GeminiAdvisor(httpClient, macroAnalyzer, tradeRepo, orderExecutor, aiOptions, logger);
        });

        // ═══════════════════════════════════════
        // 9. База данных PostgreSQL
        // ═══════════════════════════════════════
        var connectionString = configuration.GetConnectionString("DbConnectionString")
                              ?? configuration["ConnectionStrings:DbConnectionString"]
                              ?? configuration["ConnectionStrings__DbConnectionString"];

        if (string.IsNullOrEmpty(connectionString))
            Console.WriteLine("❌ КРИТИЧЕСКАЯ ОШИБКА: DbConnectionString пуст!");
        else
            Console.WriteLine($"📡 Подключение к БД: {connectionString.Split(';')[0]}");

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
    }
}