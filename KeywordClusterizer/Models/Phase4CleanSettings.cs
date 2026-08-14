namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки Phase 4.5: AI-чистка кластеров.
    /// Прогоняет каждый кластер через нейросеть: выявляет запросы, не подходящие
    /// кластеру (шаг 1), и распределяет их по другим кластерам (шаг 2).
    /// Позволяет выбрать провайдер (DeepSeek или OpenRouter) и модель.
    /// </summary>
    public class Phase4CleanSettings
    {
        /// <summary>Включает/выключает фазу чистки кластеров.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Провайдер для Phase 4.5: "deepseek" или "openrouter".
        /// "deepseek" — использует DeepSeek API (api.deepseek.com).
        /// "openrouter" — использует OpenRouter API (openrouter.ai), позволяет любые модели.
        /// </summary>
        public string Provider { get; set; } = "deepseek";

        /// <summary>Модель для Phase 4.5. Если пустая — используется модель из phase4/deepseek.</summary>
        public string Model { get; set; } = "";

        /// <summary>Температура (0.0-1.0). Если null — используется температура phase4/deepseek.</summary>
        public double? Temperature { get; set; }

        /// <summary>Max tokens. Если null — используется значение phase4/deepseek.</summary>
        public int? MaxTokens { get; set; }

        /// <summary>Включить Chain-of-Thought (thinking). Если null — используется phase4/deepseek.</summary>
        public bool? EnableThinking { get; set; }

        /// <summary>Уровень reasoning: "low" | "medium" | "high". Если null — используется phase4/deepseek.</summary>
        public string? ReasoningEffort { get; set; }

        /// <summary>Потоковый режим. Если null — используется phase4/deepseek.</summary>
        public bool? Stream { get; set; }

        /// <summary>
        /// Максимальное количество проходов по кластерам (до стабилизации).
        /// Проходы повторяются, пока есть изменения, но не более этого лимита.
        /// </summary>
        public int MaxIterations { get; set; } = 3;

        /// <summary>
        /// Количество параллельных потоков обработки кластеров (по умолчанию 10).
        /// Обычные кластеры обрабатываются одновременно до этого лимита.
        /// </summary>
        public int MaxConcurrency { get; set; } = 10;
    }
}
