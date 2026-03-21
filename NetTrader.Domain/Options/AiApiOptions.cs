using System.ComponentModel.DataAnnotations;

namespace NetTrader.Domain.Options;

/// <summary>
/// Типизированная конфигурация для Gemini AI API.
/// Привязывается к секции "AiApi" в appsettings.json.
/// </summary>
public class AiApiOptions
{
    public const string SectionName = "AiApi";

    [Required(ErrorMessage = "Gemini ApiKey обязателен")]
    public string ApiKey { get; set; } = string.Empty;

    public string PrimaryModel { get; set; } = "gemini-3.1-pro-preview";
    public string FallbackModel { get; set; } = "gemini-3-flash-preview";

    /// <summary>Таймаут на запрос к ИИ в минутах</summary>
    [Range(1, 180)]
    public int TimeoutMinutes { get; set; } = 60;
}
