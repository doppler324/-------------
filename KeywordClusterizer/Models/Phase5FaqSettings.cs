namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки Phase 5: отбор FAQ-кластеров и привязка к статьям.
    /// AI анализирует кластеры и отбирает те, что не подходят для отдельной статьи,
    /// но подходят как блок FAQ. Отобранные кластеры автоматически привязываются
    /// к ближайшей статье по смыслу (cosine similarity, без AI).
    /// </summary>
    public class Phase5FaqSettings
    {
        /// <summary>Включает/выключает Phase 5 (отбор FAQ-кластеров).</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Провайдер для AI-отбора: "deepseek" или "openrouter".
        /// "deepseek" — использует DeepSeek API (api.deepseek.com).
        /// "openrouter" — использует OpenRouter API (openrouter.ai), позволяет любые модели.
        /// </summary>
        public string Provider { get; set; } = "deepseek";

        /// <summary>Модель для Phase 5. Если пустая — используется модель из phase4/deepseek.</summary>
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
        /// Минимальный порог cosine similarity для привязки FAQ-кластера к статье (0.0-1.0).
        /// Если лучший кандидат ниже порога — FAQ остаётся без привязки (LinkedArticle = null).
        /// </summary>
        public double LinkThreshold { get; set; } = 0.55;

        /// <summary>
        /// Сколько названий кластеров отправлять нейросети за ОДИН запрос (батч).
        /// Большой список (100+ кластеров) одним запросом — медленно и ненадёжно,
        /// поэтому список разбивается на батчи и обрабатывается по отдельности.
        /// </summary>
        public int BatchSize { get; set; } = 40;
    }
}
