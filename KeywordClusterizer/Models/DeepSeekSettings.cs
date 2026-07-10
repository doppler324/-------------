namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки подключения к DeepSeek API: ключ, модель, температура, лимиты,
    /// а также параметры reasoning (thinking / reasoning_effort) для deepseek-reasoner.
    /// </summary>
    public class DeepSeekSettings
    {
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "deepseek-chat";

        /// <summary>Модель для шага 3 (Refactoring). Если пустая — используется Model.</summary>
        public string RefactoringModel { get; set; } = "";

        public double Temperature { get; set; } = 0.2;
        public int MaxTokens { get; set; } = 8192;
        public double TopP { get; set; } = 1.0;

        /// <summary>
        /// Включает Chain-of-Thought (thinking) для deepseek-reasoner.
        /// true → модель пишет внутренние рассуждения перед ответом.
        /// </summary>
        public bool EnableThinking { get; set; } = true;

        /// <summary>
        /// Уровень усилий reasoning: "low" | "medium" | "high".
        /// Выше — глубже думает, но дольше и дороже.
        /// </summary>
        public string ReasoningEffort { get; set; } = "low";

        /// <summary>
        /// true — ответ приходит токенами в реальном времени (streaming).
        /// false — ждём полный ответ (удобнее для парсинга JSON).
        /// </summary>
        public bool Stream { get; set; } = false;
    }
}
