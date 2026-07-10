namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки Phase 4: AI Merge + Naming.
    /// Позволяет выбрать провайдер (DeepSeek или OpenRouter) и модель.
    /// </summary>
    public class Phase4Settings
    {
        /// <summary>
        /// Провайдер для Phase 4: "deepseek" или "openrouter".
        /// "deepseek" — использует DeepSeek API (api.deepseek.com).
        /// "openrouter" — использует OpenRouter API (openrouter.ai), позволяет любые модели.
        /// </summary>
        public string Provider { get; set; } = "deepseek";

        /// <summary>Модель для Phase 4. Если пустая — используется _deepSeekSettings.Model.</summary>
        public string Model { get; set; } = "";

        /// <summary>Температура (0.0-1.0). Если null — используется _deepSeekSettings.Temperature.</summary>
        public double? Temperature { get; set; }

        /// <summary>Max tokens. Если null — используется _deepSeekSettings.MaxTokens.</summary>
        public int? MaxTokens { get; set; }

        /// <summary>Включить Chain-of-Thought (thinking). Если null — используется _deepSeekSettings.EnableThinking.</summary>
        public bool? EnableThinking { get; set; }

        /// <summary>Уровень reasoning: "low" | "medium" | "high". Если null — используется _deepSeekSettings.ReasoningEffort.</summary>
        public string? ReasoningEffort { get; set; }

        /// <summary>Потоковый режим. Если null — используется _deepSeekSettings.Stream.</summary>
        public bool? Stream { get; set; }
    }
}
