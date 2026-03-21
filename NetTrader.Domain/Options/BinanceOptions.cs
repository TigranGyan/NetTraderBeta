using System.ComponentModel.DataAnnotations;

namespace NetTrader.Domain.Options;

/// <summary>
/// Типизированная конфигурация для Binance API.
/// Привязывается к секции "Binance" (или "ExchangeApi") в appsettings.json.
/// </summary>
public class BinanceOptions
{
    public const string SectionName = "Binance";

    [Required(ErrorMessage = "Binance ApiKey обязателен")]
    public string ApiKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "Binance ApiSecret обязателен")]
    public string ApiSecret { get; set; } = string.Empty;

    [Url(ErrorMessage = "BaseUrl должен быть валидным URL")]
    public string BaseUrl { get; set; } = "https://fapi.binance.com";
}
