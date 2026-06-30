namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки для SERP-валидации через XmlRiver.
    /// </summary>
    public class XmlRiverSettings
    {
    /// <summary>Провайдер SERP (xmlriver).</summary>
    public string Provider { get; set; } = "xmlriver";

    /// <summary>Имя пользователя XmlRiver.</summary>
    public string XmlriverUser { get; set; } = "";

    /// <summary>API-ключ XmlRiver.</summary>
    public string XmlriverKey { get; set; } = "";

    /// <summary>Включить ли финальную SERP-проверку кластеров.</summary>
    public bool EnableValidation { get; set; } = false;

    /// <summary>
    /// Минимальный Jaccard overlap (0..1) для признания интента совпадающим.
    /// Значение по умолчанию: 0.4 (40%).
    /// </summary>
    public double MinOverlap { get; set; } = 0.4;

    /// <summary>
    /// Сколько URL из топа выдачи брать для каждого ключа.
    /// </summary>
    public int TopResultsCount { get; set; } = 5;

    /// <summary>
    /// Сколько ключей из кластера опрашивать через XmlRiver для SERP-валидации.
    /// </summary>
    public int SampleSize { get; set; } = 3;

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
    /// Включить финальную проверку: после всей перегруппировки проверить
    /// overlap во всех кластерах. Использует тот же порог minOverlap.
    /// </summary>
    public bool EnableFinalValidation { get; set; } = true;

    /// <summary>
    /// Максимальное количество параллельных запросов к XmlRiver.
    /// XmlRiver позволяет до 10 одновременных потоков.
    /// </summary>
    public int MaxConcurrency { get; set; } = 3;
    }
}
