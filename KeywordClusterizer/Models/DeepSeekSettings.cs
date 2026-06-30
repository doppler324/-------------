namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки подключения к DeepSeek API: ключ, модель, температура, лимиты.
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
    }
}
