# Используем SDK .NET 9 для сборки
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Копируем проекты и восстанавливаем зависимости
COPY ["NetTrader.Worker/NetTrader.Worker.csproj", "NetTrader.Worker/"]
COPY ["NetTrader.Application/NetTrader.Application.csproj", "NetTrader.Application/"]
COPY ["NetTrader.Infrastructure/NetTrader.Infrastructure.csproj", "NetTrader.Infrastructure/"]
COPY ["NetTrader.Domain/NetTrader.Domain.csproj", "NetTrader.Domain/"]

RUN dotnet restore "NetTrader.Worker/NetTrader.Worker.csproj"

# Копируем весь код и собираем
COPY . .
RUN dotnet publish "NetTrader.Worker/NetTrader.Worker.csproj" -c Release -o /app/publish

# Финальный образ (ASP.NET Runtime) - ИСПРАВЛЕНО ЗДЕСЬ
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/publish .

# Запуск приложения
ENTRYPOINT ["dotnet", "NetTrader.Worker.dll"]