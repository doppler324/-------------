namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки для SERP-кластеризации через XmlRiver.
    /// </summary>
    public class XmlRiverSettings
    {
        /// <summary>Провайдер SERP (xmlriver).</summary>
        public string Provider { get; set; } = "xmlriver";

        /// <summary>Имя пользователя XmlRiver.</summary>
        public string XmlriverUser { get; set; } = "";

        /// <summary>API-ключ XmlRiver.</summary>
        public string XmlriverKey { get; set; } = "";

        /// <summary>
        /// Включить SERP-first кластеризацию (через граф интентов).
        /// </summary>
        public bool EnableSerpFirst { get; set; } = true;

        /// <summary>
        /// Порог пересечения URL в топе выдачи (absolute count).
        /// Если у двух ключей совпадают >= OverlapThreshold URL из Топ-10,
        /// они считаются одним интентом.
        /// Рекомендуемое значение: 3-4.
        /// </summary>
        public int OverlapThreshold { get; set; } = 3;

        /// <summary>
        /// Сколько URL из топа выдачи брать для каждого ключа.
        /// </summary>
        public int TopResultsCount { get; set; } = 10;

        /// <summary>
        /// Включить кэширование SERP-результатов в JSON-файл.
        /// При повторных запусках не тратит API-лимиты XmlRiver.
        /// </summary>
        public bool EnableCache { get; set; } = true;

        /// <summary>
        /// Путь к файлу кэша SERP-результатов.
        /// </summary>
        public string CachePath { get; set; } = "serp_cache.json";

        /// <summary>
        /// Максимальное количество retry-попыток запроса к XmlRiver
        /// при пустом ответе (transient failures).
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Задержка между retry-попытками (в миллисекундах).
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;

        /// <summary>
        /// Максимальное количество параллельных запросов к XmlRiver.
        /// XmlRiver позволяет до 10 одновременных потоков.
        /// </summary>
        public int MaxConcurrency { get; set; } = 10;
    }
}
