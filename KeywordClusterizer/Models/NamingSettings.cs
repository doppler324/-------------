namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки отдельного режима «Наименование кластеров через ИИ» (naming из clusters.csv).
    /// Каждый кластер отправляется в нейросеть по отдельности (название + все ключи),
    /// ИИ придумывает новый H1-заголовок. Обработка параллельная (MaxConcurrency потоков).
    /// </summary>
    public class NamingSettings
    {
        /// <summary>Включает/выключает режим (по умолчанию включён — вызывается вручную).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Провайдер для AI: "deepseek" или "openrouter".
        /// "deepseek" — использует DeepSeek API (api.deepseek.com).
        /// "openrouter" — использует OpenRouter API (openrouter.ai), позволяет любые модели.
        /// </summary>
        public string Provider { get; set; } = "deepseek";

        /// <summary>Модель для наименования. Если пустая — используется модель из deepseek.</summary>
        public string Model { get; set; } = "";

        /// <summary>Температура (0.0-1.0). Если null — используется deepseek.temperature.</summary>
        public double? Temperature { get; set; }

        /// <summary>Max tokens. Если null — используется deepseek.maxTokens.</summary>
        public int? MaxTokens { get; set; }

        /// <summary>Включить Chain-of-Thought (thinking). Если null — используется deepseek.enableThinking.</summary>
        public bool? EnableThinking { get; set; }

        /// <summary>Уровень reasoning: "low" | "medium" | "high". Если null — используется deepseek.reasoningEffort.</summary>
        public string? ReasoningEffort { get; set; }

        /// <summary>Потоковый режим. Если null — используется deepseek.stream.</summary>
        public bool? Stream { get; set; }

        /// <summary>
        /// Сколько кластеров обрабатывается параллельно (потоков). По умолчанию 10.
        /// </summary>
        public int MaxConcurrency { get; set; } = 10;
    }
}
