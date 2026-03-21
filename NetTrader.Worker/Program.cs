using Microsoft.EntityFrameworkCore;
using NetTrader.Application;
using NetTrader.Infrastructure;
using NetTrader.Worker.Workers;
using Serilog;
using Serilog.Events;

// ═══ Serilog: структурированное логирование ═══
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .Enrich.WithThreadId()
    .Enrich.WithMachineName()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/nettrader-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}")
    // Раскомментируй для Seq:
    // .WriteTo.Seq("http://localhost:5341")
    .CreateLogger();

try
{
    Log.Information("🚀 NetTrader запускается...");

    var builder = Host.CreateApplicationBuilder(args);

    // Заменяем стандартный логгер на Serilog
    builder.Services.AddSerilog();

    // 1. Подключаем настройки Инфраструктуры (включая IOptions, Polly, БД и HttpClient)
    builder.Services.AddInfrastructure(builder.Configuration);

    // 2. Подключаем сервисы Приложения (бизнес-логика + FluentValidation)
    builder.Services.AddApplication();

    // 3. Регистрируем фоновые процессы
    builder.Services.AddHostedService<TradingBotWorker>();
    builder.Services.AddHostedService<TelegramListenerWorker>();

    var host = builder.Build();

    // === Авто-применение миграций + самоисцеление колонок StopLoss/TakeProfit ===
    using (var scope = host.Services.CreateScope())
    {
        var services = scope.ServiceProvider;
        try
        {
            var context = services.GetRequiredService<NetTrader.Infrastructure.AppDbContext>();
            context.Database.Migrate();

            try
            {
                context.Database.ExecuteSqlRaw(
                    @"ALTER TABLE ""TradeSessions"" ADD COLUMN IF NOT EXISTS ""StopLoss"" numeric NOT NULL DEFAULT 0;");
                context.Database.ExecuteSqlRaw(
                    @"ALTER TABLE ""TradeSessions"" ADD COLUMN IF NOT EXISTS ""TakeProfit"" numeric NOT NULL DEFAULT 0;");
                context.Database.ExecuteSqlRaw(
                    @"INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"") VALUES ('20260312000000_AddStopLossTakeProfitToTradeSession', '9.0.2') ON CONFLICT (""MigrationId"") DO NOTHING;");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ensure columns (optional): {ex.Message}");
            }

            Log.Information("✅ База данных успешно обновлена!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Ошибка при обновлении базы данных");
        }
    }

    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "💀 NetTrader упал при запуске!");
}
finally
{
    Log.CloseAndFlush();
}
