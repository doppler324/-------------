namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки подключения к OpenRouter API для получения эмбеддингов.
    /// </summary>
    public class OpenRouterSettings
    {
        /// <summary>API-ключ OpenRouter (отдельный от DeepSeek).</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Модель эмбеддингов (по умолчанию text-embedding-3-small).</summary>
        public string EmbeddingModel { get; set; } = "text-embedding-3-small";

        /// <summary>Количество измерений эмбеддинга (1536 для text-embedding-3-small).</summary>
        public int EmbeddingDimensions { get; set; } = 1536;

        /// <summary>Путь к файлу кэша эмбеддингов на диске.</summary>
        public string CachePath { get; set; } = "embeddings_cache.json";

        /// <summary>Размер одного батч-запроса (число фраз).</summary>
        public int BatchSize { get; set; } = 64;

        /// <summary>Количество параллельных потоков для запроса эмбеддингов.</summary>
        public int MaxConcurrency { get; set; } = 10;
    }
}
